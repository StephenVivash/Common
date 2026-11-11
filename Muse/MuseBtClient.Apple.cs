#if IOS || MACCATALYST || MUSE_PROTOCOL_TESTS
using System.Collections.Concurrent;
using Common.Bluetooth;

namespace Muse.Core;

public sealed partial class MuseBtClient
{
    private AppleBleTransport? _transport;
    private readonly SemaphoreSlim _controlWrites = new(1, 1);
    private bool _controlAvailable;
    private Task DisconnectedTask => _transport!.Disconnected;
    private bool IsConnected => _transport is not null && !_transport.Disconnected.IsCompleted;

    public async Task<MuseDeviceAdvertisement?> FindMuseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        MuseDeviceAdvertisement? result = null;
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var scanner = new AppleBleTransport();
        OnDiagnostic("Scanning for Muse BLE advertisements...");
        try
        {
            await scanner.ScanAsync(timeout, found =>
            {
                if (result is not null) return;
                var serviceAdvertised = found.Services.Contains(MuseBluetoothConstants.MuseServiceUuid);
                if (!serviceAdvertised && !found.Name.Contains("Muse", StringComparison.OrdinalIgnoreCase)) return;
                result = MuseDeviceAdvertisement.FromPeripheral(found.Identifier, found.Name, found.Rssi, serviceAdvertised);
                OnDiagnostic($"Found candidate: {result.DisplayName}, id={result.DeviceIdentifier}, rssi={result.Rssi} dBm, serviceAdvertised={serviceAdvertised}");
                scanCts.Cancel();
            }, scanCts.Token);
        }
        catch (OperationCanceledException) when (result is not null && !cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task ConnectAndStreamOnceAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        if (discovered.PeripheralIdentifier is not { } identifier)
            throw new ArgumentException("This Muse headset has no Apple peripheral identifier. Scan for it again.", nameof(discovered));

        var transport = new AppleBleTransport();
        _transport = transport;
        var sensors = new ConcurrentDictionary<Guid, MuseSensorDefinition>();
        transport.Notification += (uuid, bytes) =>
        {
            if (!ReferenceEquals(_transport, transport) || cancellationToken.IsCancellationRequested) return;
            if (sensors.TryGetValue(uuid, out var sensor))
                HandleNotification(uuid, sensor.Name, sensor.Kind, bytes);
        };
        transport.ConnectionFailed += error =>
        {
            if (!ReferenceEquals(_transport, transport)) return;
            OnDiagnostic($"Connection lost: {error.Message}");
            ConnectionStatusChanged?.Invoke(this, "Disconnected");
        };

        OnDiagnostic($"Connecting to {discovered.DisplayName}, id={identifier}...");
        await transport.ConnectAsync(identifier, cancellationToken);
        ConnectionStatusChanged?.Invoke(this, "Connected");
        var services = await transport.DiscoverServicesAsync(cancellationToken);
        if (!services.Contains(MuseBluetoothConstants.MuseServiceUuid))
            throw new IOException("Muse service discovery failed: service missing.");
        var characteristics = await transport.DiscoverCharacteristicsAsync(MuseBluetoothConstants.MuseServiceUuid, cancellationToken);
        OnDiagnostic($"Muse characteristics: {string.Join(", ", characteristics)}");
        if (!characteristics.Contains(MuseBluetoothConstants.ControlUuid))
            throw new IOException("Muse control characteristic is missing.");
        _controlAvailable = true;

        await SubscribeAsync(new("Control", MuseBluetoothConstants.ControlUuid, MuseSensorKind.Control));
        foreach (var sensor in MuseBluetoothConstants.Sensors)
        {
            if (!characteristics.Contains(sensor.Uuid))
            {
                OnDiagnostic($"Optional characteristic {sensor.Name} missing.");
                continue;
            }
            await SubscribeAsync(sensor);
        }
        if (!sensors.Values.Any(s => s.Kind == MuseSensorKind.Eeg))
            throw new IOException("No Muse EEG characteristics were found.");
        await StartClassicMuseStreamAsync(cancellationToken);
        await RunStreamingLoopAsync(cancellationToken);

        async Task SubscribeAsync(MuseSensorDefinition sensor)
        {
            // Register before enabling notifications, which may arrive immediately.
            sensors.TryAdd(sensor.Uuid, sensor);
            await transport.SubscribeAsync(MuseBluetoothConstants.MuseServiceUuid, sensor.Uuid, cancellationToken);
            OnDiagnostic($"Subscribe {sensor.Name}: enabled");
        }
    }

    public async Task SendControlCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _controlWrites.WaitAsync(cancellationToken);
        try
        {
            var transport = _transport;
            if (transport is null || !_controlAvailable) return;
            await transport.WriteAsync(MuseBluetoothConstants.MuseServiceUuid, MuseBluetoothConstants.ControlUuid,
                EncodeControlCommand(command), preferWithoutResponse: false, cancellationToken);
            OnDiagnostic($"Control write '{command}': completed");
        }
        finally { _controlWrites.Release(); }
    }

    private async Task CleanupConnectionAsync(bool sendStopCommand)
    {
        try
        {
            if (sendStopCommand && _controlAvailable && IsConnected)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await SendControlCommandAsync("h", stopCts.Token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    OnDiagnostic($"Stop command during cleanup failed: {ex.Message}");
                }
            }
        }
        finally
        {
            var transport = _transport;
            _transport = null;
            _controlAvailable = false;
            if (transport is not null) await transport.DisposeAsync();
        }
    }
}
#endif
