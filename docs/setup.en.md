# Setting up TabSpan

TabSpan shows a Windows PC's screen on an Android device. Touching the device touches the PC at that point.
This guide covers installing the PC side and the device side and connecting them for the first time.

> [!WARNING]
> This is software under development. The only device it has been checked on is a dtab d-01J (Android 7.0).

## 1. What you need

| Item | When | How to get it |
|---|---|---|
| A Windows 11 (x64) PC | Always | — |
| .NET 10 SDK | To build the PC side from source | <https://dotnet.microsoft.com/download> |
| adb (Android SDK Platform-Tools) | To connect over USB | 2.2 below |
| An Android 7.0 or later device | Always | Turn on USB debugging in Developer options (2.3 below) |
| Virtual Display Driver | To extend the desktop | 2.1 below. Not needed for mirroring |

TabSpan never asks for administrator rights. It does not install drivers or adb, or change their settings.

## 2. Preparation

### 2.1 Virtual display (only if you want to extend)

Run this in PowerShell. If an administrator prompt appears, check it and allow it (winget installs the driver, not TabSpan).

```powershell
winget install --id=VirtualDrivers.Virtual-Display-Driver -e
```

Then press "Refresh list" under "Screen to show" in TabSpan. A monitor marked "virtual" appears. Choosing it extends the desktop; choosing a real monitor mirrors it.

### 2.2 adb (only if you connect over USB)

Run this in a normal (non-administrator) PowerShell, then open PowerShell again.

```powershell
winget install Google.PlatformTools
```

TabSpan looks for adb in this order: the path in its settings, then PATH, then the default winget location (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Google.PlatformTools_*\platform-tools\adb.exe`).

### 2.3 USB debugging on the device

1. On the device, open Settings → About → tap Build number 7 times (Developer options appear).
2. In Developer options, turn on USB debugging.
3. Connect the device to the PC by USB and choose "Allow" when it asks whether to allow USB debugging.

Where these settings are depends on the device.

## 3. Installing

1. Build the PC side (prebuilt exe files are not distributed yet).

   ```powershell
   dotnet build pc\TabSpan.sln -c Release
   ```

   This produces `pc\src\TabSpan.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\TabSpan.App.exe`. To get one folder that includes .NET, use `tools\publish.ps1` (section 6).
2. Download `TabSpan-<version>-android.apk` from the GitHub release and install it on the device.

   ```powershell
   adb install -r TabSpan-<version>-android.apk
   ```

   Use the same version on the PC and the device.

## 4. Connecting for the first time (pairing)

1. Start `TabSpan.App.exe` on the PC.
2. Choose the monitor to show under "Screen to show".
3. Press "Add device". The PC waits 120 seconds for a request.
4. Open TabSpan on the device and press "Add PC".
5. A 6-digit number appears on both the PC and the device. **Only if they match**, press "Allow" on the PC. If they differ, press "Deny".
6. The device connects to the PC by itself and the screen appears.

Pairing works only while the device is connected by USB (not over LAN).

## 5. Connecting over LAN

1. Pair over USB once first.
2. Turn on "Connect over LAN" in the PC settings.
3. **If Windows Firewall asks, allow private networks only.** Do not allow public networks (Wi-Fi in stations, shops and so on).
4. Put the device on the same network as the PC and open TabSpan. The device tries 127.0.0.1 (USB), then the last address, then an address you typed, then discovery.
5. If the PC is not found, open the device settings, pick the PC and choose "Enter the address manually", then type the PC's IPv4 address.

Over LAN, only paired devices can connect. The PC does not answer discovery from devices that are not paired.

## 6. Building a package (`tools\publish.ps1`)

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

It writes a zip of the PC folder (with .NET included) and the device APK into `dist\`, with the version in their names, and prints SHA-256 hashes.

## 7. Good to know

- Touches do not reach windows running as administrator (such as Task Manager). This is a Windows rule.
- The lock screen, UAC prompts and copy-protected video may not be shown, or may be black (not checked yet).
- If the device does not reconnect within a set time after disconnecting (30 seconds by default), TabSpan removes the virtual display from the desktop. Windows on that screen move to the remaining screens. When the device reconnects, the display is put back where it was. Real monitors are never touched.
- The video screen stays landscape even if you turn the device.
- To pair again, unpair the device in the PC settings and unpair the PC in the device settings, then repeat section 4.
