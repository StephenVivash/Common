#if WINDOWS
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace WattCycle.Core;

public sealed partial class WattCycleBtClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ulong, WattCycleScanCandidate> _scanCandidates = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;
    private GattCharacteristic? _config;
    private TaskCompletionSource? _connectionLost;

    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<string>? InfoMessage;
    public event EventHandler<string>? ConnectionStatusChanged;
    public event EventHandler<WattCycleBatteryReading>? BatteryReadingReceived;

    public async Task<WattCycleDeviceAdvertisement?> FindBatteryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<WattCycleDeviceAdvertisement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(timeout);

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName;
            var advertisesBmsService = args.Advertisement.ServiceUuids.Any(uuid => uuid == WattCycleBluetoothConstants.JbdServiceUuid);
            var discovered = new WattCycleDeviceAdvertisement(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, advertisesBmsService);
            _scanCandidates[args.BluetoothAddress] = new WattCycleScanCandidate(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, advertisesBmsService, DateTimeOffset.Now);
            var looksLikeBattery = advertisesBmsService || LooksLikeBatteryName(name);
            if (!looksLikeBattery)
            {
                return;
            }

            OnDiagnostic($"Found candidate: {discovered.DisplayName}, address=0x{discovered.BluetoothAddress:X}, rssi={discovered.Rssi} dBm, serviceAdvertised={discovered.ServiceAdvertised}");
            completion.TrySetResult(discovered);
        };
        watcher.Stopped += (_, args) =>
        {
            OnDiagnostic($"Scanner stopped: {args.Error}");
            if (args.Error != BluetoothError.Success)
            {
                completion.TrySetResult(null);
            }
        };

        await using var timeoutRegistration = timeoutCts.Token.Register(() => completion.TrySetResult(SelectFallbackScanCandidate()));
        await using var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _scanCandidates.Clear();
        OnDiagnostic("Scanning for WattCycle battery BLE advertisements...");
        watcher.Start();
        try
        {
            return await completion.Task;
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch (InvalidOperationException ex)
            {
                OnDiagnostic($"Stopping WattCycle BLE scanner failed: {ex.Message}");
            }
        }
    }

    public async Task<IReadOnlyList<WattCycleDeviceAdvertisement>> FindBatteriesAsync(
        int maxBatteries,
        TimeSpan timeout,
        Action<WattCycleDeviceAdvertisement>? batteryDiscovered = null,
        CancellationToken cancellationToken = default)
    {
        if (maxBatteries <= 0)
        {
            return Array.Empty<WattCycleDeviceAdvertisement>();
        }

        var discovered = new ConcurrentDictionary<ulong, WattCycleDeviceAdvertisement>();
        var completion = new TaskCompletionSource<IReadOnlyList<WattCycleDeviceAdvertisement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(timeout);

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName;
            var advertisesBmsService = args.Advertisement.ServiceUuids.Any(uuid => uuid == WattCycleBluetoothConstants.JbdServiceUuid);
            if (!advertisesBmsService && !LooksLikeBatteryName(name))
            {
                return;
            }

            var advertisement = new WattCycleDeviceAdvertisement(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, advertisesBmsService);
            if (discovered.TryAdd(args.BluetoothAddress, advertisement))
            {
                OnDiagnostic($"Found battery {discovered.Count}/{maxBatteries}: {advertisement.DisplayName}, address=0x{advertisement.BluetoothAddress:X}, rssi={advertisement.Rssi} dBm, serviceAdvertised={advertisement.ServiceAdvertised}");
                batteryDiscovered?.Invoke(advertisement);
            }

            if (discovered.Count >= maxBatteries)
            {
                completion.TrySetResult(OrderDiscoveredBatteries(discovered.Values, maxBatteries));
            }
        };
        watcher.Stopped += (_, args) =>
        {
            OnDiagnostic($"Scanner stopped: {args.Error}");
            if (args.Error != BluetoothError.Success)
            {
                completion.TrySetResult(OrderDiscoveredBatteries(discovered.Values, maxBatteries));
            }
        };

        await using var timeoutRegistration = timeoutCts.Token.Register(() => completion.TrySetResult(OrderDiscoveredBatteries(discovered.Values, maxBatteries)));
        await using var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        OnDiagnostic($"Scanning for up to {maxBatteries} WattCycle batteries...");
        watcher.Start();
        try
        {
            return await completion.Task;
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch (InvalidOperationException ex)
            {
                OnDiagnostic($"Stopping WattCycle BLE scanner failed: {ex.Message}");
            }
        }
    }




    private async Task ConnectForCommandAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _frameReader = new JbdFrameReader();
        _tdtFrameReader = new TdtFrameReader();
        _tdtCommandHead = 0x1e;
        _connectionLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OnDiagnostic($"Connecting to {discovered.DisplayName} for command...");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(discovered.BluetoothAddress);
        if (_device is null)
        {
            throw new InvalidOperationException("Windows could not create a BluetoothLEDevice for that address.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        OnDiagnostic($"Device name='{_device.Name}', id='{_device.DeviceId}', status={_device.ConnectionStatus}");

        var protocol = await FindProtocolAsync(cancellationToken);
        if (protocol is null)
        {
            throw new InvalidOperationException("BMS service discovery failed: none of the known service layouts matched.");
        }

        _notify = protocol.NotifyCharacteristic;
        _write = protocol.WriteCharacteristic;
        _config = protocol.ConfigCharacteristic;
        _protocolKind = protocol.Kind;
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            await UnlockTdtAsync(cancellationToken);
        }

        await SubscribeAsync(_notify, cancellationToken);
    }

    private async Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _frameReader = new JbdFrameReader();
        _tdtFrameReader = new TdtFrameReader();
        _tdtCommandHead = 0x1e;
        _connectionLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OnDiagnostic($"Connecting to {discovered.DisplayName}...");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(discovered.BluetoothAddress);
        if (_device is null)
        {
            OnInfo("Windows could not create a BluetoothLEDevice for that address.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        OnDiagnostic($"Device name='{_device.Name}', id='{_device.DeviceId}', status={_device.ConnectionStatus}");

        var protocol = await FindProtocolAsync(cancellationToken);
        if (protocol is null)
        {
            return;
        }

        _notify = protocol.NotifyCharacteristic;
        _write = protocol.WriteCharacteristic;
        _config = protocol.ConfigCharacteristic;
        _protocolKind = protocol.Kind;
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            await UnlockTdtAsync(cancellationToken);
        }

        await SubscribeAsync(_notify, cancellationToken);
        ConnectionStatusChanged?.Invoke(this, "Polling");
        await RunPollingLoopAsync(cancellationToken);
    }

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Polling WattCycle battery.");
        var connectionLostTask = _connectionLost?.Task ?? Task.Delay(Timeout.InfiniteTimeSpan);
        while (!cancellationToken.IsCancellationRequested)
        {
            var reading = await ReadBatteryAsync(cancellationToken);
            BatteryReadingReceived?.Invoke(this, reading);

            var delayTask = Task.Delay(WattCycleBluetoothConstants.PollInterval, cancellationToken);
            if (await Task.WhenAny(delayTask, connectionLostTask) == connectionLostTask)
            {
                OnDiagnostic("Connection lost; leaving poll loop for reconnect.");
                break;
            }
        }
    }







    private async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid, string name, CancellationToken cancellationToken, bool optional = false)
    {
        var result = await BluetoothReadAttempt.AwaitNativeAsync(() => service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(), cancellationToken);
        if (result.Status == GattCommunicationStatus.AccessDenied)
        {
            throw BluetoothAccessDenied($"characteristic {name}");
        }
        if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
        {
            var characteristic = result.Characteristics[0];
            OnDiagnostic($"Characteristic {name}: {characteristic.Uuid}, properties={characteristic.CharacteristicProperties}");
            return characteristic;
        }

        var message = $"Characteristic {name} missing: {result.Status}, protocolError={FormatProtocolError(result.ProtocolError)}";
        if (optional)
        {
            OnDiagnostic(message);
        }
        else
        {
            OnInfo(message);
        }

        return null;
    }

    private async Task WriteCommandAsync(byte[] bytes, bool preferWithoutResponse, CancellationToken cancellationToken)
    {
        var characteristic = _write ?? throw new IOException("Write characteristic is not connected.");
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        var option = preferWithoutResponse && characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            || !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        var status = await BluetoothReadAttempt.AwaitNativeAsync(() => characteristic.WriteValueAsync(writer.DetachBuffer(), option).AsTask(), cancellationToken);
        if (status != GattCommunicationStatus.Success) throw new IOException($"BMS command write failed: {status}");
    }

    private async Task UnlockTdtAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            OnDiagnostic("TDT config characteristic FFFA missing; continuing without HiLink unlock.");
            return;
        }

        using var writer = new DataWriter();
        writer.WriteBytes(TdtProtocol.UnlockCommand);
        var status = await BluetoothReadAttempt.AwaitNativeAsync(() => _config.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(), cancellationToken);
        OnDiagnostic($"TDT unlock write HiLink: {status}");
        if (_config.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            var read = await BluetoothReadAttempt.AwaitNativeAsync(() => _config.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(), cancellationToken);
            var bytes = read.Status == GattCommunicationStatus.Success ? ReadBytes(read.Value) : [];
            OnDiagnostic($"TDT unlock read FFFA: {read.Status}, value={Convert.ToHexString(bytes)}");
        }
    }

    private async Task<JbdFrame?> TryReadNotifyCharacteristicAsync(byte register, CancellationToken cancellationToken)
    {
        if (_notify is null || !_notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            return null;
        }

        var result = await BluetoothReadAttempt.AwaitNativeAsync(() => _notify.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(), cancellationToken);
        OnDiagnostic($"Read notify characteristic after timeout: {result.Status}, protocolError={FormatProtocolError(result.ProtocolError)}");
        if (result.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        var bytes = ReadBytes(result.Value);
        OnDiagnostic($"Read notify value: {Convert.ToHexString(bytes)}");
        foreach (var rawFrame in _frameReader.Add(bytes))
        {
            if (JbdProtocol.TryParseFrame(rawFrame, out var frame) && frame.Register == register)
            {
                return frame;
            }
        }

        return null;
    }

    private async Task<ResolvedWindowsProtocol?> FindProtocolAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            return null;
        }

        foreach (var definition in WattCycleBluetoothConstants.Protocols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var servicesResult = await _device.GetGattServicesForUuidAsync(definition.ServiceUuid, BluetoothCacheMode.Uncached);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (servicesResult.Status == GattCommunicationStatus.AccessDenied)
                {
                    throw BluetoothAccessDenied(definition.Name);
                }
                if (servicesResult.Status == GattCommunicationStatus.Unreachable)
                {
                    throw new IOException("Battery unreachable during service discovery.");
                }
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    OnDiagnostic($"{definition.Name} service not found: {servicesResult.Status}, protocolError={FormatProtocolError(servicesResult.ProtocolError)}");
                    continue;
                }

                var service = servicesResult.Services[0];
                OnDiagnostic($"{definition.Name} service discovered: {service.Uuid}");
                var access = await BluetoothReadAttempt.AwaitNativeAsync(() => service.RequestAccessAsync().AsTask(), cancellationToken);
                OnDiagnostic($"{definition.Name} service access: {access}");
                if (access != DeviceAccessStatus.Allowed)
                {
                    throw BluetoothAccessDenied($"{definition.Name}: {access}");
                }
                var notify = await FindCharacteristicAsync(service, definition.NotifyCharacteristicUuid, "Notify", cancellationToken);
                var write = definition.WriteCharacteristicUuid == definition.NotifyCharacteristicUuid
                    ? notify
                    : await FindCharacteristicAsync(service, definition.WriteCharacteristicUuid, "Write", cancellationToken);
                var config = definition.ConfigCharacteristicUuid.HasValue
                    ? await FindCharacteristicAsync(service, definition.ConfigCharacteristicUuid.Value, "Config", cancellationToken, optional: true)
                    : null;
                if (notify is not null && write is not null)
                {
                    OnDiagnostic($"Using {definition.Name}: notify={notify.Uuid}, write={write.Uuid}");
                    // Keep the service alive with its characteristics, then release it at disconnect.
                    _service = service;
                    return new ResolvedWindowsProtocol(definition.Kind, notify, write, config);
                }
            }
            finally
            {
                // Rejected layouts and surplus instances must not retain Windows GATT access.
                foreach (var service in servicesResult.Services)
                {
                    if (!ReferenceEquals(service, _service))
                    {
                        service.Dispose();
                    }
                }
            }
        }

        await LogAllServicesAsync(cancellationToken);
        OnInfo("BMS service discovery failed: none of the known service layouts matched.");
        return null;
    }

    private async Task LogAllServicesAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnDiagnostic($"All service discovery: {result.Status}, protocolError={FormatProtocolError(result.ProtocolError)}, count={result.Services.Count}");
            foreach (var service in result.Services)
            {
                OnDiagnostic($"Service {service.Uuid}");
                var characteristics = await BluetoothReadAttempt.AwaitNativeAsync(() => service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), cancellationToken);
                OnDiagnostic($"  Characteristics: {characteristics.Status}, protocolError={FormatProtocolError(characteristics.ProtocolError)}, count={characteristics.Characteristics.Count}");
                foreach (var characteristic in characteristics.Characteristics)
                {
                    OnDiagnostic($"  Characteristic {characteristic.Uuid}, properties={characteristic.CharacteristicProperties}");
                }
            }
        }
        finally
        {
            foreach (var service in result.Services)
            {
                service.Dispose();
            }
        }
    }

    private async Task SubscribeAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        characteristic.ValueChanged += OnCharacteristicValueChanged;

        var descriptorValue = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await BluetoothReadAttempt.AwaitNativeAsync(() => characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue).AsTask(), cancellationToken);
        OnDiagnostic($"Subscribe Notify: {status}");
        if (status != GattCommunicationStatus.Success)
        {
            throw new IOException($"Could not subscribe to battery notifications: {status}.");
        }
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        // Ignore a notification queued before the old connection was released.
        if (!ReferenceEquals(sender, _notify)) return;
        var bytes = ReadBytes(args.CharacteristicValue);
        HandleNotification(bytes);
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        _ = args;
        if (!ReferenceEquals(sender, _device)) return;
        var status = sender.ConnectionStatus.ToString();
        ConnectionStatusChanged?.Invoke(this, status);
        OnDiagnostic($"Connection status: {status}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _connectionLost?.TrySetResult();
            var failure = new IOException("Windows battery connection disconnected.");
            _pendingCommand?.Completion.TrySetException(failure);
            _pendingTdtCommand?.Completion.TrySetException(failure);
        }
    }

    private async Task CleanupConnectionAsync()
    {
        await Task.CompletedTask;
        var notify = _notify;
        var service = _service;
        var device = _device;
        _notify = null;
        _write = null;
        _config = null;
        _service = null;
        _device = null;
        _connectionLost = null;
        _pendingCommand = null;
        _pendingTdtCommand = null;
        try
        {
            if (notify is not null)
            {
                notify.ValueChanged -= OnCharacteristicValueChanged;
            }
        }
        finally
        {
            try
            {
                service?.Dispose();
            }
            finally
            {
                if (device is not null)
                {
                    device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                    device.Dispose();
                }
            }
        }
    }

    private static UnauthorizedAccessException BluetoothAccessDenied(string resource) => new(
        $"Windows denied access to the battery's Bluetooth service ({resource}). " +
        "Close other Bluetooth battery connections and try again.");

    private static byte[] ReadBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }


    private WattCycleDeviceAdvertisement? SelectFallbackScanCandidate()
    {
        var candidates = _scanCandidates.Values
            .OrderByDescending(candidate => candidate.ServiceAdvertised)
            .ThenByDescending(candidate => LooksLikeBatteryName(candidate.Name))
            .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .ThenByDescending(candidate => candidate.Rssi)
            .Take(8)
            .ToArray();

        if (candidates.Length == 0)
        {
            OnDiagnostic("No BLE advertisements were seen during the WattCycle scan.");
            return null;
        }

        OnDiagnostic("No explicit WattCycle/JBD advertisement found. Nearby BLE advertisements:");
        foreach (var candidate in candidates)
        {
            var displayName = string.IsNullOrWhiteSpace(candidate.Name) ? "(no local name)" : candidate.Name;
            OnDiagnostic($"  {displayName}, address=0x{candidate.BluetoothAddress:X}, rssi={candidate.Rssi} dBm, serviceAdvertised={candidate.ServiceAdvertised}");
        }

        var fallback = candidates.FirstOrDefault(candidate => candidate.ServiceAdvertised || LooksLikeBatteryName(candidate.Name));
        if (fallback is null)
        {
            OnDiagnostic("No fallback selected because none of the nearby advertisements matched a WattCycle/JBD battery name or service.");
            return null;
        }

        OnDiagnostic($"Trying fallback BLE device '{fallback.ToAdvertisement().DisplayName}' so service discovery can verify whether it is the battery.");
        return fallback.ToAdvertisement();
    }

    private static IReadOnlyList<WattCycleDeviceAdvertisement> OrderDiscoveredBatteries(IEnumerable<WattCycleDeviceAdvertisement> advertisements, int maxBatteries) =>
        advertisements
            .OrderByDescending(advertisement => advertisement.ServiceAdvertised)
            .ThenBy(advertisement => advertisement.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(advertisement => advertisement.Rssi)
            .Take(maxBatteries)
            .ToArray();




    private static string FormatProtocolError(byte? protocolError) => protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "none";




    private sealed record ResolvedWindowsProtocol(
        BmsProtocolKind Kind,
        GattCharacteristic NotifyCharacteristic,
        GattCharacteristic WriteCharacteristic,
        GattCharacteristic? ConfigCharacteristic);
}
#elif ANDROID
using System.Collections.Concurrent;
using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;

namespace WattCycle.Core;

#pragma warning disable CA1416, CA1422
public sealed partial class WattCycleBtClient : IAsyncDisposable
{
    private static readonly UUID ClientCharacteristicConfigurationUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly ConcurrentDictionary<ulong, WattCycleScanCandidate> _scanCandidates = new();
    private BluetoothGatt? _gatt;
    private WattCycleGattCallback? _gattCallback;
    private BluetoothGattCharacteristic? _notify;
    private BluetoothGattCharacteristic? _write;
    private BluetoothGattCharacteristic? _config;

    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<string>? InfoMessage;
    public event EventHandler<string>? ConnectionStatusChanged;
    public event EventHandler<WattCycleBatteryReading>? BatteryReadingReceived;

    public async Task<WattCycleDeviceAdvertisement?> FindBatteryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var scanner = GetBluetoothAdapter()?.BluetoothLeScanner;
        if (scanner is null)
        {
            OnInfo("Android Bluetooth LE scanner is not available.");
            return null;
        }

        var completion = new TaskCompletionSource<WattCycleDeviceAdvertisement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(timeout);
        await using var timeoutRegistration = timeoutCts.Token.Register(() => completion.TrySetResult(SelectFallbackScanCandidate()));
        await using var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        var callback = new WattCycleScanCallback(discovered =>
        {
            _scanCandidates[discovered.BluetoothAddress] = new WattCycleScanCandidate(discovered.BluetoothAddress, discovered.Name, discovered.Rssi, discovered.ServiceAdvertised, DateTimeOffset.Now);
            if (discovered.ServiceAdvertised || LooksLikeBatteryName(discovered.Name))
            {
                OnDiagnostic($"Found candidate: {discovered.DisplayName}, address=0x{discovered.BluetoothAddress:X}, rssi={discovered.Rssi} dBm, serviceAdvertised={discovered.ServiceAdvertised}");
                completion.TrySetResult(discovered);
            }
        });

        _scanCandidates.Clear();
        OnDiagnostic("Scanning for WattCycle battery BLE advertisements...");
        try
        {
            scanner.StartScan(callback);
        }
        catch (Exception ex)
        {
            callback.Dispose();
            OnInfo($"Android BLE scan could not start: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }

        try
        {
            return await completion.Task;
        }
        finally
        {
            try
            {
                scanner.StopScan(callback);
            }
            catch (Exception ex)
            {
                OnDiagnostic($"Stopping Android WattCycle BLE scanner failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            callback.Dispose();
        }
    }

    public async Task<IReadOnlyList<WattCycleDeviceAdvertisement>> FindBatteriesAsync(
        int maxBatteries,
        TimeSpan timeout,
        Action<WattCycleDeviceAdvertisement>? batteryDiscovered = null,
        CancellationToken cancellationToken = default)
    {
        if (maxBatteries <= 0)
        {
            return Array.Empty<WattCycleDeviceAdvertisement>();
        }

        var scanner = GetBluetoothAdapter()?.BluetoothLeScanner;
        if (scanner is null)
        {
            OnInfo("Android Bluetooth LE scanner is not available.");
            return Array.Empty<WattCycleDeviceAdvertisement>();
        }

        var discovered = new ConcurrentDictionary<ulong, WattCycleDeviceAdvertisement>();
        var completion = new TaskCompletionSource<IReadOnlyList<WattCycleDeviceAdvertisement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(timeout);
        await using var timeoutRegistration = timeoutCts.Token.Register(() => completion.TrySetResult(OrderDiscoveredBatteries(discovered.Values, maxBatteries)));
        await using var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        var callback = new WattCycleScanCallback(advertisement =>
        {
            if (!advertisement.ServiceAdvertised && !LooksLikeBatteryName(advertisement.Name))
            {
                return;
            }

            if (discovered.TryAdd(advertisement.BluetoothAddress, advertisement))
            {
                OnDiagnostic($"Found battery {discovered.Count}/{maxBatteries}: {advertisement.DisplayName}, address=0x{advertisement.BluetoothAddress:X}, rssi={advertisement.Rssi} dBm, serviceAdvertised={advertisement.ServiceAdvertised}");
                batteryDiscovered?.Invoke(advertisement);
            }

            if (discovered.Count >= maxBatteries)
            {
                completion.TrySetResult(OrderDiscoveredBatteries(discovered.Values, maxBatteries));
            }
        });

        OnDiagnostic($"Scanning for up to {maxBatteries} WattCycle batteries...");
        try
        {
            scanner.StartScan(callback);
        }
        catch (Exception ex)
        {
            callback.Dispose();
            OnInfo($"Android BLE scan could not start: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return Array.Empty<WattCycleDeviceAdvertisement>();
        }

        try
        {
            return await completion.Task;
        }
        finally
        {
            try
            {
                scanner.StopScan(callback);
            }
            catch (Exception ex)
            {
                OnDiagnostic($"Stopping Android WattCycle BLE scanner failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            callback.Dispose();
        }
    }




    private Task ConnectForCommandAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken) =>
        ConnectAndroidAsync(discovered, cancellationToken);

    private async Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        await ConnectAndroidAsync(discovered, cancellationToken);
        ConnectionStatusChanged?.Invoke(this, "Polling");
        await RunPollingLoopAsync(cancellationToken);
    }

    private async Task ConnectAndroidAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        var adapter = GetBluetoothAdapter();
        using var device = adapter?.GetRemoteDevice(FormatBluetoothAddress(discovered.BluetoothAddress));
        if (device is null)
        {
            throw new InvalidOperationException("Android could not create a BluetoothDevice for that address.");
        }

        // A reconnect starts with fresh buffers and callback operations, not the failed session.
        _frameReader = new JbdFrameReader();
        _tdtFrameReader = new TdtFrameReader();
        _tdtCommandHead = 0x1e;
        _gattCallback = new WattCycleGattCallback(HandleCharacteristicChanged,
            status => ConnectionStatusChanged?.Invoke(this, status), HandleAndroidConnectionFailure);
        _gatt = device.ConnectGatt(Application.Context, autoConnect: false, _gattCallback, BluetoothTransports.Le);
        if (_gatt is null)
        {
            throw new InvalidOperationException("Android ConnectGatt returned null.");
        }

        await _gattCallback.WaitForConnectedAsync(cancellationToken);
        OnDiagnostic($"Connected to {discovered.DisplayName}.");
        if (!_gatt.DiscoverServices())
        {
            throw new IOException("Android BMS service discovery failed to start.");
        }
        var serviceStatus = await _gattCallback.WaitForServicesDiscoveredAsync(cancellationToken);
        if (serviceStatus != GattStatus.Success)
        {
            throw new IOException($"Android BMS service discovery failed: {serviceStatus}");
        }

        var protocol = FindProtocol();
        if (protocol is null)
        {
            throw new InvalidOperationException("BMS service discovery failed: none of the known service layouts matched.");
        }

        _notify = protocol.NotifyCharacteristic;
        _write = protocol.WriteCharacteristic;
        _config = protocol.ConfigCharacteristic;
        _protocolKind = protocol.Kind;
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            await UnlockTdtAsync(cancellationToken);
        }

        await SubscribeAsync(_notify, cancellationToken);
    }
    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Polling WattCycle battery.");
        var disconnectedTask = _gattCallback?.DisconnectedTask ?? Task.Delay(Timeout.InfiniteTimeSpan);
        while (!cancellationToken.IsCancellationRequested)
        {
            var reading = await ReadBatteryAsync(cancellationToken);
            BatteryReadingReceived?.Invoke(this, reading);

            var delayTask = Task.Delay(WattCycleBluetoothConstants.PollInterval, cancellationToken);
            if (await Task.WhenAny(delayTask, disconnectedTask) == disconnectedTask)
            {
                OnDiagnostic("Connection lost; leaving poll loop for reconnect.");
                break;
            }
        }
    }







    private BluetoothGattCharacteristic? FindCharacteristic(BluetoothGattService service, Guid uuid, string name, bool optional = false)
    {
        var characteristic = service.GetCharacteristic(ToJavaUuid(uuid));
        if (characteristic is not null)
        {
            OnDiagnostic($"Characteristic {name}: {characteristic.Uuid}, properties={characteristic.Properties}");
            return characteristic;
        }

        var message = $"Characteristic {name} missing.";
        if (optional)
        {
            OnDiagnostic(message);
        }
        else
        {
            OnInfo(message);
        }

        return null;
    }

    private async Task WriteCommandAsync(byte[] bytes, bool preferWithoutResponse, CancellationToken cancellationToken)
    {
        if (_gatt is null || _write is null || _gattCallback is null) throw new IOException("Write characteristic is not connected.");
        _write.WriteType = _write.Properties.HasFlag(GattProperty.WriteNoResponse) ? GattWriteType.NoResponse : GattWriteType.Default;
        _write.SetValue(bytes);
        var completion = _gattCallback.BeginCharacteristicWrite();
        if (!_gatt.WriteCharacteristic(_write)) throw new IOException("BMS command write failed to start.");
        var status = await completion.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        if (status != GattStatus.Success) throw new IOException($"BMS command write failed: {status}");
    }

    private async Task UnlockTdtAsync(CancellationToken cancellationToken)
    {
        if (_gatt is null || _gattCallback is null || _config is null)
        {
            OnDiagnostic("TDT config characteristic FFFA missing; continuing without HiLink unlock.");
            return;
        }

        _config.WriteType = _config.Properties.HasFlag(GattProperty.WriteNoResponse) ? GattWriteType.NoResponse : GattWriteType.Default;
        _config.SetValue(TdtProtocol.UnlockCommand);
        var writeCompletion = _gattCallback.BeginCharacteristicWrite();
        if (!_gatt.WriteCharacteristic(_config))
        {
            throw new IOException("TDT unlock write HiLink failed to start.");
        }

        var status = await writeCompletion.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        OnDiagnostic($"TDT unlock write HiLink ({_config.WriteType}): {status}");
        if (status != GattStatus.Success)
        {
            throw new IOException($"TDT unlock write HiLink failed: {status}");
        }

        if (_config.Properties.HasFlag(GattProperty.Read))
        {
            var readCompletion = _gattCallback.BeginCharacteristicRead();
            if (!_gatt.ReadCharacteristic(_config))
            {
                throw new IOException("TDT unlock read FFFA failed to start.");
            }
            var read = await readCompletion.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            OnDiagnostic($"TDT unlock read FFFA: {read.Status}, value={Convert.ToHexString(read.Value)}");
            if (read.Status != GattStatus.Success)
            {
                throw new IOException($"TDT unlock read FFFA failed: {read.Status}");
            }
        }
    }

    private async Task<JbdFrame?> TryReadNotifyCharacteristicAsync(byte register, CancellationToken cancellationToken)
    {
        if (_gatt is null || _gattCallback is null || _notify is null || !_notify.Properties.HasFlag(GattProperty.Read))
        {
            return null;
        }

        var readCompletion = _gattCallback.BeginCharacteristicRead();
        if (!_gatt.ReadCharacteristic(_notify))
        {
            OnDiagnostic("Read notify characteristic after timeout failed to start.");
            return null;
        }

        var readResult = await readCompletion.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        OnDiagnostic($"Read notify characteristic after timeout: {readResult.Status}");
        if (readResult.Status != GattStatus.Success)
        {
            return null;
        }

        OnDiagnostic($"Read notify value: {Convert.ToHexString(readResult.Value)}");
        foreach (var rawFrame in _frameReader.Add(readResult.Value))
        {
            if (JbdProtocol.TryParseFrame(rawFrame, out var frame) && frame.Register == register)
            {
                return frame;
            }
        }

        return null;
    }

    private ResolvedAndroidProtocol? FindProtocol()
    {
        if (_gatt is null)
        {
            return null;
        }

        foreach (var definition in WattCycleBluetoothConstants.Protocols)
        {
            var service = _gatt.GetService(ToJavaUuid(definition.ServiceUuid));
            if (service is null)
            {
                OnDiagnostic($"{definition.Name} service not found.");
                continue;
            }

            OnDiagnostic($"{definition.Name} service discovered: {service.Uuid}");
            var notify = FindCharacteristic(service, definition.NotifyCharacteristicUuid, "Notify");
            var write = definition.WriteCharacteristicUuid == definition.NotifyCharacteristicUuid
                ? notify
                : FindCharacteristic(service, definition.WriteCharacteristicUuid, "Write");
            var config = definition.ConfigCharacteristicUuid.HasValue
                ? FindCharacteristic(service, definition.ConfigCharacteristicUuid.Value, "Config", optional: true)
                : null;
            if (notify is not null && write is not null)
            {
                OnDiagnostic($"Using {definition.Name}: notify={notify.Uuid}, write={write.Uuid}");
                return new ResolvedAndroidProtocol(definition.Kind, notify, write, config);
            }
        }

        LogAllServices();
        OnInfo("BMS service discovery failed: none of the known service layouts matched.");
        return null;
    }

    private void LogAllServices()
    {
        if (_gatt?.Services is null)
        {
            OnDiagnostic("All service discovery: no services returned by Android.");
            return;
        }

        OnDiagnostic($"All service discovery: count={_gatt.Services.Count}");
        foreach (var service in _gatt.Services)
        {
            OnDiagnostic($"Service {service.Uuid}");
            foreach (var characteristic in service.Characteristics ?? [])
            {
                OnDiagnostic($"  Characteristic {characteristic.Uuid}, properties={characteristic.Properties}");
            }
        }
    }

    private async Task SubscribeAsync(BluetoothGattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        if (_gatt is null || _gattCallback is null)
        {
            throw new IOException("Android Bluetooth connection is unavailable for notifications.");
        }

        if (!_gatt.SetCharacteristicNotification(characteristic, true))
        {
            throw new IOException("Subscribe Notify: Android refused characteristic notifications.");
        }

        using var descriptor = characteristic.GetDescriptor(ClientCharacteristicConfigurationUuid);
        if (descriptor is null)
        {
            throw new IOException("Subscribe Notify: CCCD missing.");
        }

        var descriptorValue = characteristic.Properties.HasFlag(GattProperty.Notify)
            ? BluetoothGattDescriptor.EnableNotificationValue
            : BluetoothGattDescriptor.EnableIndicationValue;
        descriptor.SetValue(descriptorValue?.ToArray() ?? Array.Empty<byte>());
        var descriptorCompletion = _gattCallback.BeginDescriptorWrite();
        if (!_gatt.WriteDescriptor(descriptor))
        {
            throw new IOException("Subscribe Notify: descriptor write failed to start.");
        }

        var status = await descriptorCompletion.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        OnDiagnostic($"Subscribe Notify: {status}");
        if (status != GattStatus.Success)
        {
            throw new IOException($"Subscribe Notify failed: {status} ({(int)status}).");
        }
    }

    private void HandleCharacteristicChanged(BluetoothGattCharacteristic characteristic, byte[] bytes) => HandleNotification(bytes);

    private void HandleAndroidConnectionFailure(Exception error)
    {
        OnDiagnostic(error.Message);
        _pendingCommand?.Completion.TrySetException(error);
        _pendingTdtCommand?.Completion.TrySetException(error);
    }

    private async Task CleanupConnectionAsync()
    {
        var gatt = _gatt;
        var callback = _gattCallback;
        callback?.BeginClose();
        _gatt = null;
        _gattCallback = null;
        _notify = null;
        _write = null;
        _config = null;
        _pendingCommand = null;
        _pendingTdtCommand = null;
        try
        {
            if (gatt is not null)
            {
                try
                {
                    gatt.Disconnect();
                    if (callback is not null)
                    {
                        // Disconnect is asynchronous. Always close even if Android never confirms it.
                        await callback.DisconnectedTask.WaitAsync(TimeSpan.FromSeconds(2));
                        OnDiagnostic("Android GATT disconnect confirmed.");
                    }
                }
                catch (Exception ex)
                {
                    OnDiagnostic($"Android GATT disconnect: {ex.GetType().Name}: {ex.Message}; closing client.");
                }
                finally
                {
                    try
                    {
                        gatt.Close();
                        OnDiagnostic("Android GATT client closed.");
                    }
                    catch (Exception ex)
                    {
                        OnDiagnostic($"Android GATT close failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        gatt.Dispose();
                    }
                }
            }
        }
        finally
        {
            callback?.Dispose();
        }
    }
    private static BluetoothAdapter? GetBluetoothAdapter()
    {
        var manager = (BluetoothManager?)Application.Context.GetSystemService(Context.BluetoothService);
        return manager?.Adapter;
    }


    private WattCycleDeviceAdvertisement? SelectFallbackScanCandidate()
    {
        var candidates = _scanCandidates.Values
            .OrderByDescending(candidate => candidate.ServiceAdvertised)
            .ThenByDescending(candidate => LooksLikeBatteryName(candidate.Name))
            .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .ThenByDescending(candidate => candidate.Rssi)
            .Take(8)
            .ToArray();

        if (candidates.Length == 0)
        {
            OnDiagnostic("No BLE advertisements were seen during the WattCycle scan.");
            return null;
        }

        OnDiagnostic("No explicit WattCycle/JBD advertisement found. Nearby BLE advertisements:");
        foreach (var candidate in candidates)
        {
            var displayName = string.IsNullOrWhiteSpace(candidate.Name) ? "(no local name)" : candidate.Name;
            OnDiagnostic($"  {displayName}, address=0x{candidate.BluetoothAddress:X}, rssi={candidate.Rssi} dBm, serviceAdvertised={candidate.ServiceAdvertised}");
        }

        var fallback = candidates.FirstOrDefault(candidate => candidate.ServiceAdvertised || LooksLikeBatteryName(candidate.Name));
        if (fallback is null)
        {
            OnDiagnostic("No fallback selected because none of the nearby advertisements matched a WattCycle/JBD battery name or service.");
            return null;
        }

        OnDiagnostic($"Trying fallback BLE device '{fallback.ToAdvertisement().DisplayName}' so service discovery can verify whether it is the battery.");
        return fallback.ToAdvertisement();
    }

    private static IReadOnlyList<WattCycleDeviceAdvertisement> OrderDiscoveredBatteries(IEnumerable<WattCycleDeviceAdvertisement> advertisements, int maxBatteries) =>
        advertisements
            .OrderByDescending(advertisement => advertisement.ServiceAdvertised)
            .ThenBy(advertisement => advertisement.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(advertisement => advertisement.Rssi)
            .Take(maxBatteries)
            .ToArray();

    private static UUID ToJavaUuid(Guid guid) => UUID.FromString(guid.ToString())!;

    private static Guid FromJavaUuid(UUID uuid) => Guid.Parse(uuid.ToString());

    private static ulong ParseBluetoothAddress(string? address) =>
        string.IsNullOrWhiteSpace(address) ? 0 : Convert.ToUInt64(address.Replace(":", "", StringComparison.Ordinal), 16);

    private static string FormatBluetoothAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }







    private sealed record ResolvedAndroidProtocol(
        BmsProtocolKind Kind,
        BluetoothGattCharacteristic NotifyCharacteristic,
        BluetoothGattCharacteristic WriteCharacteristic,
        BluetoothGattCharacteristic? ConfigCharacteristic);

    private sealed class WattCycleScanCallback : ScanCallback
    {
        private readonly Action<WattCycleDeviceAdvertisement> _onDiscovered;

        public WattCycleScanCallback(Action<WattCycleDeviceAdvertisement> onDiscovered)
        {
            _onDiscovered = onDiscovered;
        }

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            _ = callbackType;
            if (result?.Device is null)
            {
                return;
            }

            var device = result.Device;
            var record = result.ScanRecord;
            var name = record?.DeviceName ?? device.Name ?? "";
            var advertisesBmsService = record?.ServiceUuids?.Any(uuid => uuid?.Uuid is not null && FromJavaUuid(uuid.Uuid).Equals(WattCycleBluetoothConstants.JbdServiceUuid)) == true;
            _onDiscovered(new WattCycleDeviceAdvertisement(ParseBluetoothAddress(device.Address), name, (short)result.Rssi, advertisesBmsService));
        }
    }

    private sealed class WattCycleGattCallback : BluetoothGattCallback
    {
        private readonly object _gate = new();
        private readonly Action<BluetoothGattCharacteristic, byte[]> _onCharacteristicChanged;
        private readonly Action<string> _onConnectionStatusChanged;
        private readonly Action<Exception> _onConnectionFailed;
        private readonly GattSessionOperations<GattStatus, CharacteristicReadResult> _operations = new();

        public WattCycleGattCallback(Action<BluetoothGattCharacteristic, byte[]> onCharacteristicChanged,
            Action<string> onConnectionStatusChanged, Action<Exception> onConnectionFailed)
        {
            _onCharacteristicChanged = onCharacteristicChanged;
            _onConnectionStatusChanged = onConnectionStatusChanged;
            _onConnectionFailed = onConnectionFailed;
        }

        public Task WaitForConnectedAsync(CancellationToken cancellationToken) => _operations.Connected.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        public Task<GattStatus> WaitForServicesDiscoveredAsync(CancellationToken cancellationToken) => _operations.Services.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        public Task DisconnectedTask => _operations.Disconnected;
        public Task<GattStatus> BeginDescriptorWrite() => _operations.BeginDescriptorWrite();
        // NoResponse skips the peripheral ATT response, not Android's local write completion.
        public Task<GattStatus> BeginCharacteristicWrite() => _operations.BeginCharacteristicWrite();
        public Task<CharacteristicReadResult> BeginCharacteristicRead() => _operations.BeginCharacteristicRead();

        public void BeginClose()
        {
            // Wait for a callback already in progress; all later application events are ignored.
            lock (_gate) _operations.BeginClose();
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            _ = gatt;
            lock (_gate)
            {
                if (newState == ProfileState.Disconnected) _operations.ConfirmDisconnected();
                if (!_operations.IsActive) return;

                _onConnectionStatusChanged(newState.ToString());
                if (status != GattStatus.Success || newState == ProfileState.Disconnected)
                {
                    var error = new IOException($"Android GATT connection ended: state={newState}, status={status} ({(int)status}).");
                    _operations.Fail(error);
                    _onConnectionFailed(error);
                }
                else if (newState == ProfileState.Connected)
                {
                    _operations.CompleteConnected();
                }
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            _ = gatt;
            lock (_gate) _operations.CompleteServices(status);
        }

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            _ = gatt;
            lock (_gate)
            {
                if (!_operations.IsActive) return;
                if (characteristic?.GetValue() is { } value) _onCharacteristicChanged(characteristic, value);
            }
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value)
        {
            _ = gatt;
            lock (_gate)
            {
                if (_operations.IsActive) _onCharacteristicChanged(characteristic, value);
            }
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            _ = gatt;
            _ = descriptor;
            lock (_gate) _operations.CompleteDescriptorWrite(status);
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            _ = gatt;
            _ = characteristic;
            lock (_gate) _operations.CompleteCharacteristicWrite(status);
        }

        public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            _ = gatt;
            lock (_gate)
            {
                if (_operations.IsActive)
                    _operations.CompleteCharacteristicRead(new CharacteristicReadResult(status, characteristic?.GetValue() ?? Array.Empty<byte>()));
            }
        }

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, GattStatus status)
        {
            _ = gatt;
            _ = characteristic;
            lock (_gate) _operations.CompleteCharacteristicRead(new CharacteristicReadResult(status, value));
        }
    }
    private sealed record CharacteristicReadResult(GattStatus Status, byte[] Value);
}
#pragma warning restore CA1416, CA1422
#elif !IOS && !MACCATALYST
namespace WattCycle.Core;

#pragma warning disable CS0067
public sealed class WattCycleBtClient : IAsyncDisposable
{
	public event EventHandler<string>? DiagnosticMessage;
	public event EventHandler<string>? InfoMessage;
	public event EventHandler<string>? ConnectionStatusChanged;
	public event EventHandler<WattCycleBatteryReading>? BatteryReadingReceived;

	public Task<WattCycleDeviceAdvertisement?> FindBatteryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		_ = timeout;
		_ = cancellationToken;
		InfoMessage?.Invoke(this, "WattCycle Bluetooth is currently implemented for Windows, Android, iOS and Mac Catalyst only.");
		return Task.FromResult<WattCycleDeviceAdvertisement?>(null);
	}

	public Task<IReadOnlyList<WattCycleDeviceAdvertisement>> FindBatteriesAsync(
		int maxBatteries,
		TimeSpan timeout,
		Action<WattCycleDeviceAdvertisement>? batteryDiscovered = null,
		CancellationToken cancellationToken = default)
	{
		_ = maxBatteries;
		_ = timeout;
		_ = batteryDiscovered;
		_ = cancellationToken;
		InfoMessage?.Invoke(this, "WattCycle Bluetooth scanning is currently implemented for Windows, Android, iOS and Mac Catalyst only.");
		return Task.FromResult<IReadOnlyList<WattCycleDeviceAdvertisement>>(Array.Empty<WattCycleDeviceAdvertisement>());
	}

	public Task<WattCycleMosControlResult> SetMosAsync(
		WattCycleDeviceAdvertisement discovered,
		bool chargeEnabled,
		bool dischargeEnabled,
		CancellationToken cancellationToken = default)
	{
		_ = discovered;
		_ = chargeEnabled;
		_ = dischargeEnabled;
		_ = cancellationToken;
		InfoMessage?.Invoke(this, "WattCycle MOS control is currently implemented for Windows, Android, iOS and Mac Catalyst only.");
		return Task.FromException<WattCycleMosControlResult>(new PlatformNotSupportedException("WattCycle MOS control is currently implemented for Windows, Android, iOS and Mac Catalyst only."));
	}

	public Task<WattCycleBatteryReading> ReadOnceAsync(WattCycleDeviceAdvertisement discovered,
		CancellationToken cancellationToken = default) => Task.FromException<WattCycleBatteryReading>(
			new PlatformNotSupportedException("WattCycle Bluetooth reads are implemented for Windows, Android, iOS and Mac Catalyst only."));
	public Task ConnectAndPollAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
	{
		_ = discovered;
		_ = cancellationToken;
		InfoMessage?.Invoke(this, "WattCycle Bluetooth polling is currently implemented for Windows, Android, iOS and Mac Catalyst only.");
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#pragma warning restore CS0067
#endif
