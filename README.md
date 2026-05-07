# clicky-for-windows

Windows (WPF) port of **Clicky**: push-to-talk (**Ctrl+Alt**), screen context, Claude via a **Cloudflare Worker** proxy, streaming transcription, and TTS.

## Security

**Do not commit API keys.** This app has no embedded provider keys. Configure secrets on your Worker (Anthropic, ElevenLabs, AssemblyAI), deploy the Worker, then set the Worker base URL in the tray menu (`https://…workers.dev`). User settings live under `%AppData%\Clicky\` and are not part of this repository.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (matching `TargetFramework` in `ClickyWindows.csproj`)
- Windows 10 version 1809+ (`windows10.0.19041`)

## Build

```powershell
dotnet build .\ClickyWindows.csproj -c Release
```

Optional onboarding music: place `ff.mp3` in `Assets\` (see `ClickyWindows.csproj`).

## Repository layout

- Source: `Core\`, `Api\`, `Overlay\`, `SystemTray\`, `Audio\`, `ScreenCapture\`, `Input\`
- App manifest: `app.manifest`
