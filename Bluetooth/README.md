# Shared Bluetooth transport

`AppleBleTransport` provides CoreBluetooth scanning, connection, service and
characteristic discovery, subscriptions, reads, writes, and disconnect cleanup
for iOS and Mac Catalyst. All native calls and callbacks run on the main dispatch
queue. A transport owns a single peripheral session; callers serialize operations
and dispose the transport before creating a replacement after failure. Writes
without responses use CoreBluetooth backpressure, rather than waiting for an
acknowledgement callback that Apple does not deliver. Large writes are split at
the peripheral's maximum write size. Canceled or timed-out native operations
invalidate the session so delayed callbacks cannot complete a new operation.

WattCycle and Muse reference this library. Muse subscribes to its sensor
characteristics and uses the same packet decoders and EEG processing as its
Windows/Android adapters. The transport has no battery or Muse protocol
knowledge. See `../Muse/README.md` for Muse implementation and validation notes.

# WattCycle Apple support

`WattCycleBtClient.Protocol.cs` contains the common JBD/TDT command exchange,
notification framing, decoding, polling reconnect policy, one-shot read cleanup,
and MOS control/readback used by Windows, Android, iOS and Mac Catalyst.
`WattCycleBtClient.Apple.cs` selects the existing BMS layouts, unlocks TDT with
HiLink where available, and maps the transport to that shared protocol logic.
A successful TDT read header is reused; only a response timeout permits trying
the alternate header. MOS writes are never retried after an ACK timeout.

Apple supplies a peripheral UUID, not a MAC address. Advertisements retain that
UUID in `PeripheralIdentifier` for connection/retrieval. `BluetoothAddress` is a
stable derived key on Apple, retained for existing row/history consumers; never
use that key as a native connection address. WattCycleApp saves the UUID with
remembered batteries and passes it to polling and MOS control clients.

Both apps include `NSBluetoothAlwaysUsageDescription` in their Apple plists and
explicitly sign Mac Catalyst builds with the Bluetooth sandbox entitlement.
CoreBluetooth requests permission on first use and reports denied permission or
a powered-off adapter through the existing client error/status path. The polling
loop is foreground activity; iOS can suspend it when the app enters background.

# Validation

Run the offline checks without connecting hardware:

```sh
dotnet run --project BtHack/Tests/ProtocolChecks.csproj
```

These exercise captured packets, fragmented replies, both protocols, learned
TDT headers, native failures/cancellation, explicit rejection, missing MOS ACKs
with readback, cleanup, and stable Apple identifiers.

Build each application for `net10.0-maccatalyst` and `net10.0-ios`. For simulator
builds use `iossimulator-arm64`; physical iOS devices require normal signing.
An iOS simulator build checks compilation/packaging but cannot validate BLE.

On a physical Mac or iPhone, close other apps connected to the battery, scan,
and check voltage/current/cell readings. Stop/restart monitoring, relaunch with
a remembered battery, and test Bluetooth off/on and denied permission. Verify
MOS controls and readback only when switching the battery's load is acceptable.
No live battery switches were changed during automated validation.

The installed 26.5 Apple SDK caches compiled manifests without including the
main Info.plist in its incremental input list. When checking changed privacy
text against an existing build, clean/rebuild the Apple target and inspect the
packaged Info.plist and signed entitlements.

# Mac Catalyst Release startup recovery

On 2026-10-14, an existing arm64 Release build aborted in Mono startup before
application code ran. The AOT loader reported that `LiveChartsCore` expected a
different `System.Private.CoreLib` module GUID from the one in the app bundle.
A clean rebuild of the app and its references regenerated the cached AOT code
and restored startup and live battery reads. The graph page also rendered.

On 2026-10-24 the same failure recurred after further development. WattCycleApp
now imports `Build/AppleAotCache.targets`, which extends the 26.5 SDK's AOT cache
key with the paths and SHA256 hashes of all its managed AOT assembly inputs.
Changed dependencies invalidate the native AOT output, even when a dependent
library's own DLL is unchanged or file timestamps are preserved. Unchanged
builds keep using the cache. This applies only to Mac Catalyst Release builds;
the existing AOT, trimming, and symbol stripping settings are retained.

The next normal Release build repairs existing stale outputs automatically.
Builds with changed assemblies can take longer because the complete AOT output
must be regenerated. The target uses Apple SDK build hooks, so verify it again
when upgrading that SDK. Its offline regression checks require Python 3 and
the .NET SDK, but no Apple SDK or Bluetooth hardware:

```sh
python3 WattCycleApp/Build/check_aot_cache.py
```

If an SDK upgrade introduces a different cache problem, the manual clean-build
recovery is still available:

```sh
dotnet build WattCycleApp/WattCycleApp.csproj -t:Rebuild -c Release \
  -f net10.0-maccatalyst -p:TargetFrameworks=net10.0-maccatalyst \
  -p:RuntimeIdentifier=maccatalyst-arm64
```

A successful build alone does not verify startup; launch the resulting Release
app and check both the battery and graph pages.
