# TabSpan

Turns an Android tablet into an extended monitor for a Windows PC. [日本語](README.md)

> **Work in progress. Nothing here is usable yet.**
> The repository currently contains only the PC-side measurement tool (probe) used to settle the design. The PC app and the Android app are not written yet.

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
| Phase 0 | Probes | PC side builds. **It has never been run yet.** Android side not written |
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

## License

MIT ([LICENSE](LICENSE)). Third-party licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
