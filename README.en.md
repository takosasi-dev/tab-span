# TabSpan

Turns an Android tablet into an extended monitor for a Windows PC. [日本語](README.md)

> **Work in progress. Nothing here is usable yet.**
> The repository currently contains only the measurement tools (probes) used to settle the design, for the PC side and the Android side. The app itself is not written yet.

## What it will be

The PC side (a C# / .NET 10 / WPF tray app) captures a screen, encodes it as H.264 and sends it. The Android side (Kotlin) decodes it full screen and sends touches back. It connects over USB (adb port forwarding) or LAN.

Design rules:

- No display driver is written, installed or reconfigured. You pick a virtual display you installed yourself as the screen to send. Picking a real monitor works as a mirror with no driver at all.
- The PC side never runs as administrator.
- All traffic is TLS, even over USB, and only paired devices get through. The first pairing must happen over USB.
- Video, touch coordinates and screen contents are never stored. Nothing is sent anywhere except the paired device.

## Status

| Phase | Content | State |
|---|---|---|
| Phase 0 | Probes | PC side and Android side written. Ran monitors, encoders, capture, encode (software), adb, tls and the Android probe on a home PC and a dtab (Android 7.0). detach / attach, touch, clock and virtual displays are still to do |
| Phase 1–6 | The app | Not started |

## What is here now: the PC-side probe

Requirements: Windows 11 (x64), .NET 10 SDK.

```powershell
dotnet build pc\TabSpan.sln -c Release
```

Run `TabSpan.Probe.exe` with no arguments to see the usage. Run it from a non-elevated PowerShell.

| Subcommand | What it does |
|---|---|
| `monitors` | Lists monitors |
| `capture <id>` | Captures that monitor for 5 seconds and reports frame count and whether the capture border could be turned off |
| `encoders` | Lists H.264 encoders and the properties they support |
| `encode <id>` | Captures and encodes for 5 seconds and reports frame count, bytes and timing |
| `detach <id>` / `attach <id>` | Detaches / attaches a monitor from the desktop (asks first) |
| `touch <x> <y>` | Injects touch input at that position (asks first) |
| `adb` | Shows where adb is, its version and connected devices (serials masked) |
| `clock` | Shows a millisecond clock window (for measuring latency by photo) |
| `tls <port>` | Opens a TLS listener on 127.0.0.1 for 60 seconds (for a connection test from the device) |

Notes:

- `detach` really removes a monitor from the desktop. It refuses the primary monitor and the last remaining one, but try it on a virtual display.
- `touch` really injects touch input.
- `capture` and `encode` never write captured images to disk. They only print counts and timings.
- Probe output contains monitor names from your PC. Do not paste it in public places as is.

## What is here now: the Android-side probe

Lists the device's `video/avc` decoders, checks each candidate size (including a configure attempt), and shows the screen size. "TLS のテスト" (TLS test) tries a TLS connection to the PC's `tls`. Every line is also written to logcat (tag `TabSpanProbe`).

Build (JDK 17 and the Android SDK; put `sdk.dir` in `android\local.properties`):

```powershell
cd android
.\gradlew.bat assembleDebug
```

TLS test:

1. On the PC, run `TabSpan.Probe.exe tls 47815`.
2. Run `adb reverse tcp:47815 tcp:47815` so the device's 127.0.0.1:47815 reaches the PC.
3. Tap "TLS のテスト" in the app, or from adb: `adb shell am start -n io.github.takosasidev.tabspan.probe/.MainActivity --es action tls --ei port 47815`.
4. Compare the certificate fingerprint (SHA-256) shown on the PC and on the device.

Notes:

- The only permission is INTERNET, and it only connects to 127.0.0.1.
- The TLS test does not validate the certificate (probe only). Check the peer by comparing the fingerprints.

## Download

The v0.0.1 pre-release on [Releases](https://github.com/takosasi-dev/tab-span/releases) has the Android probe APK only.

- The APK is **a temporary build signed with a debug key**. Once it moves to the real key you will need to uninstall it and install again.
- The PC exe is not distributed, because the redistribution terms of the Windows SDK DLLs that ship next to it have not been checked yet. Build it from source as above.

## License

MIT ([LICENSE](LICENSE)). Third-party licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
