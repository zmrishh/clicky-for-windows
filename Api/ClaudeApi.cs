using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClickyWindows.Core;

namespace ClickyWindows.Api;

/// <summary>
/// Sends vision requests to Claude via the Cloudflare Worker proxy.
/// Uses streaming SSE first; if no text deltas are captured (worker/proxy quirks, API changes),
/// falls back to a single non‑streaming Messages response.
/// </summary>
public sealed class ClaudeApi : IDisposable
{
    private readonly Uri _apiUri;
    private readonly HttpClient _client;
    private volatile bool _tlsWarmStarted;

    public string Model { get; set; }

    public ClaudeApi(string proxyBaseUrl, string model = AppConstants.DefaultClaudeModel)
    {
        _apiUri = new Uri(proxyBaseUrl.TrimEnd('/') + "/chat");
        Model = model;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(300)
        };

        WarmUpTlsIfNeeded(proxyBaseUrl);
    }

    /// <summary>
    /// Streaming first, then JSON fallback — returns assistant text suitable for POINT parsing/TTS.
    /// </summary>
    public async Task<string> AnalyzeVisionTextAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? conversationHistory,
        string userPrompt,
        Action<string> onTextChunk,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        AppDebugLog.Write($"Claude: streaming start images={images.Count} model={Model}");
        string streamed;
        try
        {
            (streamed, _) = await AnalyzeImageStreamingAsync(
                images, systemPrompt, conversationHistory, userPrompt, onTextChunk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"Claude: streaming FAILED {ex.Message}");
            streamed = "";
        }

        AppDebugLog.Write($"Claude: streaming done chars={streamed.Length} ms={sw.ElapsedMilliseconds}");

        if (!string.IsNullOrWhiteSpace(streamed.Trim()))
            return streamed.Trim();

        AppDebugLog.Write("Claude: empty stream → non-streaming fallback");
        var fallback = await AnalyzeImageNonStreamingAsync(
                images, systemPrompt, conversationHistory, userPrompt, cancellationToken).ConfigureAwait(false);

        AppDebugLog.Write($"Claude: fallback chars={fallback.Length}");
        return fallback.Trim();
    }

    public async Task<(string Text, TimeSpan Duration)> AnalyzeImageStreamingAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? conversationHistory,
        string userPrompt,
        Action<string> onTextChunk,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;

        var bodyRoot = BuildRequestBody(images, systemPrompt, conversationHistory, userPrompt, stream: true);
        var json = JsonSerializer.Serialize(bodyRoot);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, _apiUri) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Claude API error ({(int)response.StatusCode}): {err}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new IoStreamReader(stream);

        var accumulated = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmedLine = line.TrimStart('\ufeff'); // BOM
            if (!TrySliceSseData(trimmedLine, out var jsonSlice))
                continue;

            if (jsonSlice == "[DONE]")
                break;

            try
            {
                using var doc = JsonDocument.Parse(jsonSlice);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                    continue;

                var eventType = typeProp.GetString();
                if (eventType != "content_block_delta")
                    continue;

                if (!root.TryGetProperty("delta", out var delta))
                    continue;
                if (!delta.TryGetProperty("type", out var deltaType) ||
                    deltaType.GetString() != "text_delta")
                    continue;
                if (!delta.TryGetProperty("text", out var textProp))
                    continue;

                var chunk = textProp.GetString() ?? string.Empty;
                accumulated.Append(chunk);
                onTextChunk(accumulated.ToString());
            }
            catch (JsonException)
            {
                // Non-JSON pings / partial lines — ignore
            }
        }

        return (accumulated.ToString(), DateTime.UtcNow - started);
    }

    public async Task<string> AnalyzeImageNonStreamingAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? conversationHistory,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var bodyRoot = BuildRequestBody(images, systemPrompt, conversationHistory, userPrompt, stream: false);
        var json = JsonSerializer.Serialize(bodyRoot);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync(_apiUri, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Claude API (non-stream) ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("content", out var contentArr))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in contentArr.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "text" &&
                block.TryGetProperty("text", out var textEl))
            {
                sb.Append(textEl.GetString());
            }
        }

        return sb.ToString();
    }

    /// <returns>Slice after optional <c>data:</c> SSE prefix.</returns>
    private static bool TrySliceSseData(string line, out string jsonSlice)
    {
        jsonSlice = "";

        ReadOnlySpan<char> s = line.AsSpan().TrimStart();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        jsonSlice = s["data:".Length..].TrimStart().ToString();
        return true;
    }

    private Dictionary<string, object> BuildRequestBody(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? history,
        string userPrompt,
        bool stream)
    {
        var messages = new List<Dictionary<string, object>>();

        if (history != null)
        {
            foreach (var (userPh, assistantResp) in history)
            {
                messages.Add(new() { ["role"] = "user", ["content"] = userPh });
                messages.Add(new() { ["role"] = "assistant", ["content"] = assistantResp });
            }
        }

        var contentBlocks = new List<Dictionary<string, object>>();
        foreach (var (data, label) in images)
        {
            var mediaType = DetectMediaType(data);
            contentBlocks.Add(new()
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object>
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = Convert.ToBase64String(data)
                }
            });
            contentBlocks.Add(new() { ["type"] = "text", ["text"] = label });
        }
        contentBlocks.Add(new() { ["type"] = "text", ["text"] = userPrompt });

        messages.Add(new() { ["role"] = "user", ["content"] = contentBlocks });

        return new Dictionary<string, object>
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["stream"] = stream,
            ["system"] = systemPrompt,
            ["messages"] = messages
        };
    }

    private static string DetectMediaType(byte[] data)
    {
        if (data.Length >= 4 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        return "image/jpeg";
    }

    private void WarmUpTlsIfNeeded(string baseUrl)
    {
        if (_tlsWarmStarted) return;
        _tlsWarmStarted = true;

        var warmupUri = new Uri($"{baseUrl.Trim().TrimEnd('/')}/", UriKind.Absolute);
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, warmupUri);
                req.Headers.ConnectionClose = false;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _client.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                /* best-effort */
            }
        });
    }

    public void Dispose() => _client.Dispose();
}
