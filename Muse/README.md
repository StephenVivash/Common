# Muse Bluetooth

Muse supports Windows, Android, iOS and Mac Catalyst through the same
`MuseBtClient` API. `MuseBtClient.Shared.cs` owns command framing, the classic
Muse `h`, `s`, `p21`, `d` startup sequence, five-second keepalives, reconnect
backoff, notification counting, packet diagnostics and EEG band calculation.
After eight seconds with no notifications it reconnects. Every new connection
starts with empty EEG windows and packet statistics. Stopping attempts `h` for
up to two seconds before releasing the native session.

`MuseBtClient.cs` contains Windows/Android GATT adapters.
`MuseBtClient.Apple.cs` uses `Common.Bluetooth.AppleBleTransport`, the same
CoreBluetooth backend as WattCycle. It scans by Muse service or local name,
requires the control characteristic and at least one EEG characteristic, and
subscribes to the available control/EEG/IMU/telemetry/PPG characteristics.
Characteristics absent on a headset model are skipped. Muse packet decoders
and band processing are shared across all platforms; the Apple transport has
no Muse protocol knowledge.

Apple advertisements retain their full UUID in `PeripheralIdentifier`.
`BluetoothAddress` is zero on Apple and must not be used to connect. Use
`DeviceIdentifier` for diagnostic text. Windows/Android keep their existing
Bluetooth addresses. Reconnection retrieves the same Apple UUID using a new
transport after disposing the failed session. Old Apple session callbacks are
ignored. Permission denial is reported to the caller, rather than retried.

Yijing includes `NSBluetoothAlwaysUsageDescription` in both Apple Info.plists
and signs Mac Catalyst with `com.apple.security.device.bluetooth`. CoreBluetooth
requests permission on first use. The current recording flow runs in the
foreground; iOS may suspend it in the background.

## Offline validation

From Common:

```sh
dotnet run --project Muse.Tests/Muse.ProtocolChecks.csproj
```

The executable compiles the actual Apple Muse adapter and shared client against
a simulated transport, without a Bluetooth radio or headset. It checks service
and name discovery, scan timeout/cancellation and permission errors, intact UUID
connections, exact startup command bytes, subscription routing, EEG band output,
command limits, disconnect and stall recovery, keepalives, clean FFT windows,
late notifications, failed setup/writes and bounded stop cleanup.

From Yijing, build the Apple applications using each referenced project's normal
targets (do not override `TargetFrameworks` globally):

```sh
dotnet build Yijing.maui/Yijing.maui.csproj -t:Rebuild -f net10.0-maccatalyst \
  -p:RuntimeIdentifier=maccatalyst-arm64
dotnet build Yijing.maui/Yijing.maui.csproj -t:Rebuild -f net10.0-ios \
  -p:RuntimeIdentifier=iossimulator-arm64
```

A clean Apple rebuild refreshes the cached compiled privacy manifest. Inspect
the packaged Info.plist and signed Mac entitlements after changing permissions.
The app excludes the desktop MSAL dylib injected by NativeInterop's host-based
MAUI targets from iOS; that macOS binary cannot be linked into iOS builds.
Physical iOS device builds require the usual signing/provisioning.

Validation on 2026-10-15: 51 offline Muse checks passed; full Debug Mac Catalyst
arm64 and iOS simulator arm64 rebuilds passed; Windows Muse compilation passed.
Android source compilation against the installed Android reference assemblies
passed, but a full Android build was unavailable because the Android SDK/JDK
were not installed. Packaged Apple privacy text and signed Mac Bluetooth
entitlements were checked. Live Muse hardware streaming remains to be verified.

## Physical headset check

1. Close other apps connected to the Muse, turn it on, open Yijing and select
   Muse / Live. Accept the Bluetooth permission prompt.
2. Check that discovery progresses to Connected and EEG band data appears.
   Allow the existing two-minute eyes-open baseline to finish before judging
   normalized band levels. Check raw EEG and available sensor fields in a recording.
3. Stop and restart Live. Turn the headset off/on to check reconnection; confirm
   a resumed recording receives new samples. Test adapter off/on and denied
   permission, then restore permission in system settings.
4. Check on a physical iPhone/iPad as well as Mac. The simulator validates
   compilation and packaging only; it does not verify Bluetooth streaming.
