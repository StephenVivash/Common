# WattCycle MOS control investigation — 2026-10-07

## Failure identified

The supplied discovery log identifies XDZN_001_E620 as TDT over FFF0,
with FFF1 notifications, FFF2 writes, and the HiLink unlock on FFFA.
Reads beginning `1E000103008C` and `1E000103008D` succeed.
The screenshot shows `protocol=Tdt` but sends `DD5AE1020000FF1D77`,
a JBD E1 write. It receives no BMS acknowledgement and both switches
remain enabled. GATT write success alone does not mean a command applied.

The polling log also closes before the MOS attempt. BtHack now creates a
separate `WattCycleMosDebug-yyyyMMdd-HHmmss.log` for each confirmed attempt.

## Primary evidence

Inspected `Wattcycle_11_11_betaV4.apk`, linked by WattCycle's support team:

- [Official support post](https://www.wattcycle.com/community/xenforum/topic/204367/wattcycle-20251111-beta-v1-upgrade)
- [APK download page](https://drive.google.com/file/d/1Z9Uxnr8bNKNpLerI_4mcARAdD3mWc-Tt/view)
- SHA256: `2A8DEDC8FF4444C0E5AA6801414F7E1EB0A761743F274ADA63F2506C7A5A9557`

Static inspection using Android SDK dexdump, without running the APK:

| Method in `com.gz.wattcycle.repository.WattBleProtocolRepository` | Finding |
| --- | --- |
| `setChargeSwitch` (source lines 1961–1966) | Address 130 (`0082`), one byte containing the boolean |
| `setDischargeSwitch` (1989–1994) | Address 131 (`0083`), one byte containing the boolean |
| `buildWriteFrame` (505–522) | Detected header, version 0, device 1, function 6, big-endian address and data length, payload, big-endian Modbus CRC16, tail `0D` |
| `handleChargeSwitchResponse`, `handleDischargeSwitchResponse` | Success requires function 6 and zero data length |

`WarningInfo.StatusRegister3.Companion.fromByte` confirms charge mask `02`
and discharge mask `04`, matching the existing decoder. Captured `mosRaw=0E`
therefore has both enabled.

The [community protocol reference](https://github.com/qume/wattcycle_ble/blob/main/PROTOCOL.md)
was a useful starting point, but its address table disagrees with this APK:
it calls `0083` restart. Use the inspected app's switch methods as the evidence
for these commands; compatibility still needs testing on the user's battery.

## Frames for this battery's working `1E` header

| Action | TX hex |
| --- | --- |
| Charge off | `1E00010600820001001EB20D` |
| Charge on | `1E0001060082000101DE730D` |
| Discharge off | `1E0001060083000100E2B30D` |
| Discharge on | `1E000106008300010122720D` |

Expected empty acknowledgements with response header `7E` and version 0:
charge `7E000106008200005AEF0D`, discharge `7E000106008300009ABE0D`.

## Implemented behavior and validation

Windows and Android share the frame builder. Before control, a read establishes
the working header and logs the initial states. Charge and discharge are written
sequentially. Each waits for its matching function/address acknowledgement.
Explicit errors or unexpected acknowledgement payloads fail the operation;
earlier writes may already have applied. An acknowledgement timeout is logged
as unknown and does not cause a repeated write. After both writes, fresh readings
are checked up to three times for the requested states.

Offline checks cover all eight switch/header vectors, real captured readings,
all four MOS readback states, empty/error acknowledgements, bad CRCs, truncated
frames, and split/coalesced notifications. Run from the BtHack directory:

```powershell
dotnet run --project Tests\ProtocolChecks.csproj
```

Windows BtHack and the Android WattCycle library build without warnings/errors.
No live battery switch operation has been performed as part of this change.

## Next battery test

Close the official phone app's battery connection. Launch the rebuilt BtHack,
scan/read, then stop polling. Test Charge Off, then MOS Both On, followed by
Discharge Off and MOS Both On. Finally test MOS Both Off and restore MOS Both On
when ready to interrupt the load. The existing confirmation shows both target
states: Charge Off also requests discharge ON, and Discharge Off requests charge ON.

Keep each new MOS log. We need the two transmitted frames, acknowledgements or
timeouts, and the subsequent `8D` response and `matches` result. If behavior differs
from the official app, capture its Android Bluetooth HCI traffic to compare the
firmware-specific exchange rather than guessing additional registers.

## WattCycleApp failures — 2026-10-12

The 19:16 screenshot for XDZN_001_200E reports charge ON and discharge OFF
after requesting both OFF. This is a partial readback, not proof that both
commands failed. No control log exists for that attempt; do not infer an
unrecorded acknowledgement or change command addresses from the screenshot.

`WattCycleControl-20261012-191956-916.log` records a separate E620 attempt.
FFF0 was discovered at +0.155 seconds, but all its characteristic queries
returned `AccessDenied`. Discovery failed at +0.601 seconds before the unlock,
subscription, reads, or MOS writes. This explains the nearly immediate failure.

The Windows client previously neither retained/disposed GATT services nor
removed its anonymous notification handler. The client now retains its selected
service until cleanup, disposes rejected/surplus/diagnostic service instances,
and detaches the notification handler before releasing the service and device.
This fixes an observed resource leak; whether it caused E620's access denial
requires another live test. [Microsoft documents that disposing a GATT service
releases its resources for other clients](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattdeviceservice.close).
Service access is now explicitly requested and logged before characteristic
discovery, following the [Microsoft Bluetooth client sample](https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/BluetoothLE/cs/Scenario2_Client.xaml.cs).
Access denial and failed notification subscription produce specific errors.

WattCycleApp saves each selected On/Off operation to
`WattCycleControl-yyyyMMdd-HHmmss-fff.log` in its app data directory, including
the elapsed time, connection, transmitted frames, acknowledgements, readback,
and full exception. On this Windows installation that directory is
`C:\Users\Stephen Vivash\AppData\Local\User Name\com.companyname.wattcycleapp\Data`.

Windows WattCycleApp builds successfully with the existing ToolBar nullability
warnings, and all 57 offline protocol checks pass. Fully exit WattCycleApp and
BtHack to release the old handles, start the rebuilt WattCycleApp, then repeat
the intended operation on E620 and 200E. Inspect the new logs before changing
the MOS protocol or considering a partial result successful.

## Android polling recovery — 2026-10-12

The battery row's status text is display-only. Each scheduled read already
creates a new client, so retaining an error label does not choose a different
connection path. There were several faults in Android's connection recovery:

- A disconnect after connection completed did not finish pending discovery,
  descriptor, read, write, or BMS frame waits. These could remain blocked until
  the page's overall timeout.
- Disconnect was immediately followed by close, and an exception from disconnect
  could skip close/disposal. Late callbacks could still reach the client or row.
- Automatic reconnect reused partial protocol frame buffers. A read command
  timeout also escaped the reconnect loop instead of starting a fresh connection.
- Native write completion was skipped for `NoResponse` writes, allowing the next
  GATT operation to start before Android finished the previous one. Android's
  [write API documents its completion callback for both write types](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#writeCharacteristic(android.bluetooth.BluetoothGattCharacteristic,%20byte[],%20int));
  this callback is separate from the battery protocol acknowledgement.
- Failed notification subscription was logged but polling continued without a
  working subscription.

Android now uses an independent operation state for each native connection,
fails all pending waits when that connection drops, and ignores application
callbacks after cleanup starts. Cleanup waits at most two seconds for the actual
disconnect, then attempts close and disposal even if disconnect throws or never
confirms. Android [requires closing a finished GATT client](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#close()).
Reconnects create fresh readers and callback state, and polling retries read
timeouts using its existing delay. Connect/discovery waits are bounded to 15
seconds; native reads, writes and notification setup are bounded to five seconds
or the caller's earlier cancellation. Every characteristic write now waits for
Android's local completion before another operation starts. Control commands
still are not automatically retried after an uncertain write.

Validation: 57 existing protocol checks and 81 simulated Android recovery checks
pass. The new checks cover failures before connection, during discovery, during
pending operations, delayed callbacks, cleanup/disconnect confirmation, and 12
successive failed sessions followed by fresh successful sessions. Both Android
and Windows app builds pass; Windows retains its existing ToolBar warnings.
Actual recovery across repeated failures still needs testing on the phone and
batteries, without restarting the app between failures.

The user subsequently confirmed Android works as well as Windows, with occasional
failures comparable to the official app.

## Battery controls UI — 2026-10-12

The battery control dialog now shows the battery name and three standard MAUI
buttons: On, Off, Cancel. Cancellation or dismissal sends no command. It uses
the already installed Community Toolkit's [result-returning popup](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/maui/views/popup/popup-result).
Control failures and uncertain switch readbacks are reported only on the battery's
status line; diagnostic logging remains available. That status stays visible while
monitoring reconnects and clears on the next successful reading. Charge/discharge
columns use C/D labels and 24-unit widths, leaving the other three columns for
voltage, current and power.
