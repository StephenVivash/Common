#if IOS || MACCATALYST
using Common.Bluetooth;

namespace WattCycle.Core;

public sealed partial class WattCycleBtClient : IAsyncDisposable
{
    private AppleBleTransport? _transport;
    private BmsProtocolDefinition? _definition;
    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<string>? InfoMessage;
    public event EventHandler<string>? ConnectionStatusChanged;
    public event EventHandler<WattCycleBatteryReading>? BatteryReadingReceived;

    public async Task<WattCycleDeviceAdvertisement?> FindBatteryAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        (await FindBatteriesAsync(1, timeout, cancellationToken: cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<WattCycleDeviceAdvertisement>> FindBatteriesAsync(int maxBatteries, TimeSpan timeout,
        Action<WattCycleDeviceAdvertisement>? batteryDiscovered = null, CancellationToken cancellationToken = default)
    {
        if (maxBatteries <= 0) return [];
        var batteries = new Dictionary<Guid, WattCycleDeviceAdvertisement>();
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var scanner = new AppleBleTransport();
        OnDiagnostic($"Scanning for up to {maxBatteries} WattCycle batteries...");
        try
        {
            await scanner.ScanAsync(timeout, found =>
            {
                if (batteries.Count >= maxBatteries) return;
                var serviceAdvertised = found.Services.Any(id => WattCycleBluetoothConstants.Protocols.Any(p => p.ServiceUuid == id));
                if (!serviceAdvertised && !LooksLikeBatteryName(found.Name)) return;
                // Apple never exposes MAC addresses. This stable key keeps existing
                // history/UI consumers compatible; connection always uses the full UUID.
                var advertisement = WattCycleDeviceAdvertisement.FromPeripheral(found.Identifier, found.Name, found.Rssi, serviceAdvertised);
                var added = batteries.TryAdd(found.Identifier, advertisement);
                if (!added) batteries[found.Identifier] = advertisement;
                else
                {
                    OnDiagnostic($"Found battery: {advertisement.DisplayName}, id={found.Identifier}, rssi={found.Rssi} dBm");
                    batteryDiscovered?.Invoke(advertisement);
                }
                if (batteries.Count >= maxBatteries) scanCts.Cancel();
            }, scanCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && batteries.Count >= maxBatteries) { }
        cancellationToken.ThrowIfCancellationRequested();
        return batteries.Values.OrderByDescending(a => a.ServiceAdvertised)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).Take(maxBatteries).ToArray();
    }

    private async Task ConnectForCommandAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        if (discovered.PeripheralIdentifier is not { } identifier)
            throw new IOException("This battery has no Apple peripheral identifier. Scan for it again.");
        _frameReader = new();
        _tdtFrameReader = new();
        _tdtCommandHead = 0x1e;
        var transport = new AppleBleTransport();
        _transport = transport;
        transport.Notification += (id, bytes) =>
        {
            if (ReferenceEquals(_transport, transport) && id == _definition?.NotifyCharacteristicUuid) HandleNotification(bytes);
        };
        transport.ConnectionFailed += error =>
        {
            _pendingCommand?.Completion.TrySetException(error);
            _pendingTdtCommand?.Completion.TrySetException(error);
            ConnectionStatusChanged?.Invoke(this, "Disconnected");
        };
        OnDiagnostic($"Connecting to {discovered.DisplayName}, id={identifier}...");
        await transport.ConnectAsync(identifier, cancellationToken);
        ConnectionStatusChanged?.Invoke(this, "Connected");
        var services = await transport.DiscoverServicesAsync(cancellationToken);
        var characteristics = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var definition in WattCycleBluetoothConstants.Protocols)
        {
            if (!services.Contains(definition.ServiceUuid)) continue;
            if (!characteristics.TryGetValue(definition.ServiceUuid, out var ids))
                characteristics[definition.ServiceUuid] = ids = await transport.DiscoverCharacteristicsAsync(definition.ServiceUuid, cancellationToken);
            OnDiagnostic($"Service {definition.ServiceUuid}: {string.Join(", ", ids)}");
            if (!ids.Contains(definition.NotifyCharacteristicUuid) || !ids.Contains(definition.WriteCharacteristicUuid)) continue;
            _definition = definition;
            _protocolKind = definition.Kind;
            OnDiagnostic($"Using {definition.Name}.");
            if (definition.Kind == BmsProtocolKind.Tdt && definition.ConfigCharacteristicUuid is { } config && ids.Contains(config))
            {
                await transport.WriteAsync(definition.ServiceUuid, config, TdtProtocol.UnlockCommand, true, cancellationToken);
                var unlockValue = await transport.ReadAsync(definition.ServiceUuid, config, cancellationToken);
                OnDiagnostic($"TDT unlock HiLink: {(unlockValue is null ? "not readable" : Convert.ToHexString(unlockValue))}");
            }
            await transport.SubscribeAsync(definition.ServiceUuid, definition.NotifyCharacteristicUuid, cancellationToken);
            return;
        }
        throw new IOException("BMS service discovery failed: none of the known service layouts matched.");
    }


    private async Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        await ConnectForCommandAsync(discovered, cancellationToken);
        ConnectionStatusChanged?.Invoke(this, "Polling");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = await ReadBatteryAsync(cancellationToken);
            BatteryReadingReceived?.Invoke(this, reading);
            var delay = Task.Delay(WattCycleBluetoothConstants.PollInterval, cancellationToken);
            if (await Task.WhenAny(delay, _transport!.Disconnected) != delay) return;
            await delay;
        }
    }

    private Task WriteCommandAsync(byte[] bytes, bool preferWithoutResponse, CancellationToken cancellationToken)
    {
        var definition = _definition ?? throw new IOException("BMS characteristics are not connected.");
        return (_transport ?? throw new IOException("Bluetooth is not connected.")).WriteAsync(
            definition.ServiceUuid, definition.WriteCharacteristicUuid, bytes, preferWithoutResponse, cancellationToken);
    }
    private async Task<JbdFrame?> TryReadNotifyCharacteristicAsync(byte register, CancellationToken cancellationToken)
    {
        if (_transport is null || _definition is null) return null;
        var bytes = await _transport.ReadAsync(_definition.ServiceUuid, _definition.NotifyCharacteristicUuid, cancellationToken);
        if (bytes is null) return null;
        foreach (var rawFrame in _frameReader.Add(bytes))
            if (JbdProtocol.TryParseFrame(rawFrame, out var frame) && frame.Register == register) return frame;
        return null;
    }
    private async Task CleanupConnectionAsync()
    {
        var transport = _transport;
        _transport = null;
        _definition = null;
        var failure = new IOException("Bluetooth connection is closing.");
        _pendingCommand?.Completion.TrySetException(failure);
        _pendingTdtCommand?.Completion.TrySetException(failure);
        _pendingCommand = null;
        _pendingTdtCommand = null;
        if (transport is not null) await transport.DisposeAsync();
    }
}
#endif
