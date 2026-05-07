using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickyWindows.Core;

namespace ClickyWindows.Api;

// ── JSON ────────────────────────────────────────────────────────────────────

internal static class AaiJsonOptions
{
    /// <summary>AssemblyAI Turn payloads may add fields; tolerate minor API drift.</summary>
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed class AaiMessageEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

internal sealed class AaiTurnMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("transcript")] public string? Transcript { get; set; }
    [JsonPropertyName("turn_order")] public int? TurnOrder { get; set; }
    [JsonPropertyName("end_of_turn")] public bool? EndOfTurn { get; set; }
    [JsonPropertyName("turn_is_formatted")] public bool? TurnIsFormatted { get; set; }
}

internal sealed class AaiErrorMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ── Session interface ─────────────────────────────────────────────────────────

/// <summary>Matches the Swift BuddyStreamingTranscriptionSession protocol.</summary>
public interface IStreamingTranscriptionSession
{
    double FinalTranscriptFallbackDelaySeconds { get; }
    void AppendAudioData(byte[] pcm16Data);
    void RequestFinalTranscript();
    void Cancel();
}

// ── Provider ──────────────────────────────────────────────────────────────────

/// <summary>
/// Fetches a short-lived AssemblyAI token from the Worker proxy and opens a
/// WebSocket streaming session. Mirrors Swift AssemblyAIStreamingTranscriptionProvider.
/// </summary>
public sealed class AssemblyAIStreamingProvider
{
    private readonly string _tokenProxyUrl;
    private readonly HttpClient _http;

    public AssemblyAIStreamingProvider(string workerBaseUrl)
    {
        _tokenProxyUrl = workerBaseUrl.TrimEnd('/') + "/transcribe-token";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Fetches a temporary token, opens the WebSocket, and returns a ready session.
    /// </summary>
    public async Task<IStreamingTranscriptionSession> StartSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        var token = await FetchTemporaryTokenAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[AssemblyAI] Got token: {token[..Math.Min(20, token.Length)]}...");

        var session = new AssemblyAISession(
            token, keyterms, onTranscriptUpdate, onFinalTranscriptReady, onError);

        await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<string> FetchTemporaryTokenAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenProxyUrl);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AssemblyAI token fetch failed (HTTP {(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("token", out var tokenProp))
            throw new InvalidOperationException("Invalid token response from proxy.");

        return tokenProp.GetString()
               ?? throw new InvalidOperationException("Token was null in proxy response.");
    }
}

// ── Session implementation ────────────────────────────────────────────────────

/// <summary>
/// Full port of the Swift AssemblyAIStreamingTranscriptionSession state machine.
/// Identical protocol handling: turn messages, end_of_turn, explicit ForceEndpoint,
/// grace-period deadline, fallback timer, and clean Terminate on cancel.
/// </summary>
internal sealed class AssemblyAISession : IStreamingTranscriptionSession
{
    public double FinalTranscriptFallbackDelaySeconds { get; } =
        AppConstants.AssemblyAiFallbackDelaySeconds;

    private readonly string _token;
    private readonly IReadOnlyList<string> _keyterms;
    private readonly Action<string> _onTranscriptUpdate;
    private readonly Action<string> _onFinalTranscriptReady;
    private readonly Action<Exception> _onError;

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly object _stateLock = new();

    // Mirrors Swift stored-turn state machine
    private readonly Dictionary<int, (string Text, bool IsFormatted)> _storedTurns = new();
    private int? _activeTurnOrder;
    private string _activeTurnText = "";
    private string _latestTranscriptText = "";

    private bool _hasDeliveredFinalTranscript;
    private bool _hasResolvedReady;
    private bool _isAwaitingExplicit;

    private TaskCompletionSource<bool>? _readyTcs;
    private CancellationTokenSource? _gracePeriodCts;

    internal AssemblyAISession(
        string token,
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError)
    {
        _token = token;
        _keyterms = keyterms;
        _onTranscriptUpdate = onTranscriptUpdate;
        _onFinalTranscriptReady = onFinalTranscriptReady;
        _onError = onError;
    }

    // ── Open ─────────────────────────────────────────────────────────────────

    internal async Task OpenAsync(CancellationToken ct)
    {
        var url = BuildWebSocketUrl();
        _ws = new ClientWebSocket();
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _ws.ConnectAsync(url, ct).ConfigureAwait(false);

        // Start the receive loop in the background
        _ = Task.Run(() => ReceiveLoopAsync(CancellationToken.None));

        // Wait until the server sends the "begin" message (or error)
        await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    // ── Audio ─────────────────────────────────────────────────────────────────

    public void AppendAudioData(byte[] pcm16Data)
    {
        if (_ws?.State != WebSocketState.Open) return;
        _ = SendBinaryAsync(pcm16Data);
    }

    // ── Stop / Cancel ─────────────────────────────────────────────────────────

    public void RequestFinalTranscript()
    {
        lock (_stateLock)
        {
            if (_hasDeliveredFinalTranscript) return;
            _isAwaitingExplicit = true;
            ScheduleGracePeriodDeadline();
        }

        _ = SendJsonAsync(new { type = "ForceEndpoint" });
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            _gracePeriodCts?.Cancel();
        }

        _ = SendJsonAsync(new { type = "Terminate" });
        _ws?.Abort();
    }

    // ── Receive loop ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        // Multi-frame WebSocket text messages may split UTF-8 across fragments; decoding each
        // fragment separately can corrupt characters and break JSON.parse → dead session.
        await using var messageBytes = new IoMemoryStream();

        try
        {
            while (_ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    messageBytes.SetLength(0);
                    continue;
                }

                messageBytes.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage) continue;

                if (messageBytes.Length > 0)
                {
                    var payload = messageBytes.ToArray();
                    messageBytes.SetLength(0);
                    HandleMessage(Encoding.UTF8.GetString(payload));
                }
                else
                {
                    messageBytes.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleReceiveError(ex);
        }
    }

    private void HandleMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString()?.ToLowerInvariant() ?? "";

            switch (type)
            {
                case "begin":
                    ResolveReadyIfNeeded(success: true);
                    break;

                case "turn":
                    var turn = JsonSerializer.Deserialize<AaiTurnMessage>(text, AaiJsonOptions.Relaxed)
                               ?? throw new JsonException("Turn message deserialization returned null.");
                    HandleTurnMessage(turn);
                    break;

                case "termination":
                    ResolveReadyIfNeeded(success: true);
                    lock (_stateLock)
                    {
                        if (_isAwaitingExplicit && !_hasDeliveredFinalTranscript)
                            DeliverFinalTranscriptIfNeeded(BestAvailableTranscript());
                    }
                    break;

                case "error":
                    var err = JsonSerializer.Deserialize<AaiErrorMessage>(text, AaiJsonOptions.Relaxed)
                              ?? throw new JsonException("Error message deserialization returned null.");
                    var msg = err.Error ?? err.Message ?? "AssemblyAI returned an error.";
                    FailSession(new InvalidOperationException(msg));
                    break;
            }
        }
        catch (Exception ex)
        {
            FailSession(ex);
        }
    }

    // ── Turn state machine (direct port of Swift) ─────────────────────────────

    private void HandleTurnMessage(AaiTurnMessage turn)
    {
        var transcriptText = turn.Transcript?.Trim() ?? "";

        lock (_stateLock)
        {
            var turnOrder = turn.TurnOrder
                            ?? _activeTurnOrder
                            ?? ((_storedTurns.Keys.Count > 0 ? _storedTurns.Keys.Max() : -1) + 1);

            var endOfTurn = turn.EndOfTurn == true || turn.TurnIsFormatted == true;

            if (endOfTurn)
            {
                _activeTurnOrder = null;
                _activeTurnText = "";
                StoreTurn(transcriptText, turnOrder, isFormatted: turn.TurnIsFormatted == true);
            }
            else
            {
                _activeTurnOrder = turnOrder;
                _activeTurnText = transcriptText;
            }

            var full = ComposeFullTranscript();
            _latestTranscriptText = full;

            if (!string.IsNullOrEmpty(full))
                _onTranscriptUpdate(full);

            if (!_isAwaitingExplicit) return;

            if (endOfTurn)
            {
                _gracePeriodCts?.Cancel();
                _gracePeriodCts = null;
                DeliverFinalTranscriptIfNeeded(BestAvailableTranscript());
            }
        }
    }

    private void StoreTurn(string text, int order, bool isFormatted)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_storedTurns.TryGetValue(order, out var existing) && existing.IsFormatted && !isFormatted)
            return;

        _storedTurns[order] = (text, isFormatted);
    }

    private string ComposeFullTranscript()
    {
        var segments = _storedTurns
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var active = _activeTurnText.Trim();
        if (!string.IsNullOrEmpty(active))
            segments.Add(active);

        return string.Join(" ", segments);
    }

    private string BestAvailableTranscript()
    {
        var composed = ComposeFullTranscript().Trim();
        return !string.IsNullOrEmpty(composed) ? composed : _latestTranscriptText.Trim();
    }

    // ── Grace period deadline ─────────────────────────────────────────────────

    private void ScheduleGracePeriodDeadline()
    {
        _gracePeriodCts?.Cancel();
        _gracePeriodCts = new CancellationTokenSource();
        var ct = _gracePeriodCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(AppConstants.AssemblyAiExplicitFinalGracePeriodSeconds),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            lock (_stateLock)
            {
                DeliverFinalTranscriptIfNeeded(BestAvailableTranscript());
            }
        });
    }

    private void DeliverFinalTranscriptIfNeeded(string text)
    {
        // Must be called under _stateLock
        if (_hasDeliveredFinalTranscript) return;
        _hasDeliveredFinalTranscript = true;
        _gracePeriodCts?.Cancel();
        _gracePeriodCts = null;
        _onFinalTranscriptReady(text);
        _ = SendJsonAsync(new { type = "Terminate" });
    }

    // ── Error handling ────────────────────────────────────────────────────────

    private void HandleReceiveError(Exception ex)
    {
        ResolveReadyIfNeeded(success: false, ex);

        lock (_stateLock)
        {
            if (_isAwaitingExplicit && !_hasDeliveredFinalTranscript)
            {
                var partial = BestAvailableTranscript();
                if (!string.IsNullOrEmpty(partial))
                {
                    Console.WriteLine($"[AssemblyAI] ⚠️ WebSocket error during session, delivering partial: {ex.Message}");
                    DeliverFinalTranscriptIfNeeded(partial);
                    return;
                }
            }
        }

        Console.WriteLine($"[AssemblyAI] ❌ Session failed: {ex.Message}");
        _onError(ex);
    }

    private void FailSession(Exception ex) => HandleReceiveError(ex);

    // ── Ready TCS ────────────────────────────────────────────────────────────

    private void ResolveReadyIfNeeded(bool success, Exception? ex = null)
    {
        lock (_stateLock)
        {
            if (_hasResolvedReady) return;
            _hasResolvedReady = true;
        }

        if (success)
            _readyTcs?.TrySetResult(true);
        else
            _readyTcs?.TrySetException(ex ?? new Exception("Session failed"));
    }

    // ── Send helpers ──────────────────────────────────────────────────────────

    private async Task SendJsonAsync<T>(T payload)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssemblyAI] Send failed: {ex.Message}");
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    private async Task SendBinaryAsync(byte[] data)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Fire-and-forget; audio gaps are acceptable
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    // ── URL builder ──────────────────────────────────────────────────────────

    private Uri BuildWebSocketUrl()
    {
        var sb = new StringBuilder(AppConstants.AssemblyAiWebSocketBase);
        sb.Append("?sample_rate=16000&encoding=pcm_s16le&format_turns=true&speech_model=u3-rt-pro");
        sb.Append($"&token={Uri.EscapeDataString(_token)}");

        var validKeyterms = _keyterms
            .Select(k => k.Trim())
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validKeyterms.Count > 0)
        {
            var keytermsJson = JsonSerializer.Serialize(validKeyterms);
            sb.Append($"&keyterms_prompt={Uri.EscapeDataString(keytermsJson)}");
        }

        return new Uri(sb.ToString());
    }
}
