# BeamCast

**Open-source AirPlay 1 (RAOP) transmitter for Windows 11** — a TuneBlade successor.

## What it does

Streams **Windows system audio** (WASAPI loopback) to existing AirPlay 1 receivers: WiiM Mini (primary test device), Apple TV, HomePod, AirPort Express, and other RAOP speakers.

### Product pillars

1. **A/V sync** — delay the *picture* in the browser to match wireless speaker latency; PC audio stays live.
2. **Optional PC mute** — off by default (muting the master volume also mutes WASAPI loopback).
3. **Lossless ALAC** — 16-bit / 44.1 kHz baseline.
4. **No Apple Bonjour** — own mDNS/DNS-SD sockets.

## Requirements

- **Windows 11** (WinUI 3 app). Core + tests also run on Linux CI.
- An **AirPlay 1 / RAOP** speaker on the LAN (WiiM Mini confirmed).
- **.NET 10 SDK** to build from source.
- Chrome or Edge for the optional **beamcast-sync** extension.

## Stack

- **.NET 10 LTS**, C# / WinUI 3 (Windows App SDK, unpackaged, self-contained)
- **WASAPI** loopback (CsWin32) → format convert → **ALAC** (Apple encoder port, Apache-2.0) → **RAOP/RTP**
- **Bouncy Castle** — RSA/AES/Digest when the receiver requires encryption (`et=1`)
- Tests: **xUnit** + headless `MockRaopReceiver`

```
src/
  AirNext.Core/        # RAOP, mDNS, audio pipeline (no UI)
  AirNext.Core.Tests/  # xUnit
  AirNext.Audio/       # WASAPI capture/render (Windows)
  AirNext.App/         # WinUI 3 tray + window
  AirNext.StreamProbe/ # CLI live-stream probe
  AirNext.CaptureProbe/# WASAPI / latency-test probe
extensions/beamcast-sync/  # Chrome MV3 video delay
```

## Build

```bash
# Core + tests (Linux / macOS / Windows)
dotnet test src/AirNext.Core.Tests/AirNext.Core.Tests.csproj -c Release

# App — Windows 11 only (XamlCompiler). On Win11:
dotnet build src/AirNext.App/AirNext.App.csproj -c Debug
```

Run the app from the output exe (do not rely on `dotnet run` for WinUI):

```
src\AirNext.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AirNext.App.exe
```

### Installer (Windows 11)

No admin. Installs to `%LOCALAPPDATA%\Programs\BeamCast`.

```powershell
winget install JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Then run `artifacts\BeamCast-Setup.exe`. Start Menu shortcut **BeamCast**. Uninstall from Settings → Apps.

Log: `%LOCALAPPDATA%\BeamCast\beancast.log`  
Settings: `%LOCALAPPDATA%\BeamCast\settings.json`

Close **TuneBlade** while testing (it holds UDP 6002/6003).

## Chrome extension (lip-sync)

Unpacked load: `extensions/beamcast-sync/` (chrome://extensions → Developer mode → Load unpacked).

The app serves `http://127.0.0.1:46382/metrics` (`delayMs`, streaming state). The extension delays **video frames** (canvas ring) and **never pauses** `<video>` audio — WASAPI needs it.

- YouTube: status chip in `.ytp-right-controls` (left of settings / fullscreen).
- Other sites: HUD, bottom-right.

Reload the extension after each pull.

## Usage

- **Click a compatible device** (port 7000) or **Start** — streams system audio.
- **Stop** — TEARDOWN. **Pause** — FLUSH without a full reconnect.
- **Auto-stream** last-used device on launch (Advanced, on by default). WiiM/Linkplay often takes ~45–60 s to answer mDNS.
- **RealTime** must be on **before** Start. Delay slider 20–250 ms (default 40). Normal mode uses a ~3 s sync anchor.
- **Mute PC while streaming** is off by default — master-volume mute also silences WASAPI loopback.
- Closing the window hides to the tray (stream keeps running). Right-click the tray icon → **Exit** to quit.
- Close TuneBlade while testing (it holds UDP 6002/6003).

## License

[MIT](LICENSE). The ALAC encoder is a port of Apple’s reference encoder (Apache-2.0).
