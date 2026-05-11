#if WINDOWS
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WattCycle.Core;

public sealed class WattCycleBtClient : IAsyncDisposable
{
    private readonly JbdFrameReader _frameReader = new();
    private readonly TdtFrameReader _tdtFrameReader = new();
    private readonly ConcurrentDictionary<ulong, WattCycleScanCandidate> _scanCandidates = new();
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;
    private GattCharacteristic? _config;
    private TaskCompletionSource? _connectionLost;
    private PendingCommand? _pendingCommand;
    private PendingTdtCommand? _pendingTdtCommand;
    private BmsProtocolKind _protocolKind = BmsProtocolKind.Jbd;

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

    public async Task ConnectAndPollAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
    {
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (reconnectAttempt > 0)
                {
                    OnDiagnostic($"Reconnect attempt {reconnectAttempt} to {discovered.DisplayName}...");
                }

                await ConnectAndPollOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                OnInfo($"WattCycle BT poll failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await CleanupConnectionAsync();

    private async Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        _connectionLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OnDiagnostic($"Connecting to {discovered.DisplayName}...");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(discovered.BluetoothAddress);
        if (_device is null)
        {
            OnInfo("Windows could not create a BluetoothLEDevice for that address.");
            return;
        }

        _device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        OnDiagnostic($"Device name='{_device.Name}', id='{_device.DeviceId}', status={_device.ConnectionStatus}");

        var protocol = await FindProtocolAsync();
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

        await SubscribeAsync(_notify);
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

    private async Task<WattCycleBatteryReading> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            var statusFrame = await SendTdtReadCommandAsync(TdtProtocol.StatusCommand, cancellationToken);
            var protectionFrame = await SendTdtReadCommandAsync(TdtProtocol.ProtectionCommand, cancellationToken);
            OnDiagnostic(TdtProtocol.DescribeStatusFields(statusFrame, protectionFrame));
            if (TdtProtocol.TryBuildReading(statusFrame, protectionFrame, out var reading))
            {
                return reading;
            }

            throw new InvalidOperationException($"Could not decode TDT BMS info: status={ToHexFrame(statusFrame)}, protection={ToHexFrame(protectionFrame)}");
        }

        var basicFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.BasicInfoRegister, cancellationToken);
        if (!JbdProtocol.TryParseBasicInfo(basicFrame, out var basic))
        {
            throw new InvalidOperationException($"Could not decode basic BMS info: {ToHexFrame(basicFrame)}");
        }

        var cellFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.CellVoltagesRegister, cancellationToken);
        if (!JbdProtocol.TryParseCellVoltages(cellFrame, out var cellVoltages))
        {
            OnDiagnostic($"Could not decode cell voltages: {ToHexFrame(cellFrame)}");
            cellVoltages = Array.Empty<double>();
        }

        return new WattCycleBatteryReading(
            DateTimeOffset.Now,
            basic.PackVoltage,
            basic.Current,
            basic.RemainingCapacityAh,
            basic.NominalCapacityAh,
            basic.StateOfChargePercent,
            basic.CycleCount,
            basic.ChargeMosEnabled,
            basic.DischargeMosEnabled,
            cellVoltages,
            basic.TemperaturesCelsius,
            basic.ProtectionStatus);
    }

    private async Task<TdtFrame> SendTdtReadCommandAsync(byte command, CancellationToken cancellationToken)
    {
        if (_write is null)
        {
            throw new InvalidOperationException("Write characteristic is not connected.");
        }

        foreach (var commandHead in new byte[] { 0x1e, 0x7e })
        {
            var completion = new TaskCompletionSource<TdtFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTdtCommand = new PendingTdtCommand(command, completion);

            using var writer = new DataWriter();
            var frame = TdtProtocol.BuildReadCommand(command, commandHead);
            writer.WriteBytes(frame);
            var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
            var status = await _write.WriteValueAsync(writer.DetachBuffer(), option).AsTask(cancellationToken);
            OnDiagnostic($"TDT read command 0x{command:X2} write ({option}, head=0x{commandHead:X2}): {status}, frame={Convert.ToHexString(frame)}");
            if (status != GattCommunicationStatus.Success)
            {
                _pendingTdtCommand = null;
                continue;
            }

            try
            {
                return await completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                OnDiagnostic($"TDT read command 0x{command:X2} timed out with head=0x{commandHead:X2}.");
            }
            finally
            {
                if (_pendingTdtCommand?.Command == command)
                {
                    _pendingTdtCommand = null;
                }
            }
        }

        throw new TimeoutException($"TDT read command 0x{command:X2} timed out.");
    }

    private async Task<JbdFrame> SendReadCommandAsync(byte register, CancellationToken cancellationToken)
    {
        if (_write is null)
        {
            throw new InvalidOperationException("Write characteristic is not connected.");
        }

        var completion = new TaskCompletionSource<JbdFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommand = new PendingCommand(register, completion);

        using var writer = new DataWriter();
        var command = JbdProtocol.BuildReadCommand(register);
        writer.WriteBytes(command);
        var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        var status = await _write.WriteValueAsync(writer.DetachBuffer(), option).AsTask(cancellationToken);
        OnDiagnostic($"Read register 0x{register:X2} write ({option}): {status}, frame={Convert.ToHexString(command)}");
        if (status != GattCommunicationStatus.Success)
        {
            _pendingCommand = null;
            throw new InvalidOperationException($"Read register 0x{register:X2} write failed: {status}");
        }

        try
        {
            return await completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            var readFrame = await TryReadNotifyCharacteristicAsync(register, cancellationToken);
            if (readFrame is not null)
            {
                return readFrame;
            }

            throw;
        }
        finally
        {
            if (_pendingCommand?.Register == register)
            {
                _pendingCommand = null;
            }
        }
    }

    private async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid, string name, bool optional = false)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
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

    private async Task UnlockTdtAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            OnDiagnostic("TDT config characteristic FFFA missing; continuing without HiLink unlock.");
            return;
        }

        using var writer = new DataWriter();
        writer.WriteBytes(TdtProtocol.UnlockCommand);
        var status = await _config.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(cancellationToken);
        OnDiagnostic($"TDT unlock write HiLink: {status}");
        if (_config.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            var read = await _config.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
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

        var result = await _notify.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
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

    private async Task<ResolvedWindowsProtocol?> FindProtocolAsync()
    {
        if (_device is null)
        {
            return null;
        }

        foreach (var definition in WattCycleBluetoothConstants.Protocols)
        {
            var servicesResult = await _device.GetGattServicesForUuidAsync(definition.ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                OnDiagnostic($"{definition.Name} service not found: {servicesResult.Status}, protocolError={FormatProtocolError(servicesResult.ProtocolError)}");
                continue;
            }

            var service = servicesResult.Services[0];
            OnDiagnostic($"{definition.Name} service discovered: {service.Uuid}");
            var notify = await FindCharacteristicAsync(service, definition.NotifyCharacteristicUuid, "Notify");
            var write = definition.WriteCharacteristicUuid == definition.NotifyCharacteristicUuid
                ? notify
                : await FindCharacteristicAsync(service, definition.WriteCharacteristicUuid, "Write");
            var config = definition.ConfigCharacteristicUuid.HasValue
                ? await FindCharacteristicAsync(service, definition.ConfigCharacteristicUuid.Value, "Config", optional: true)
                : null;
            if (notify is not null && write is not null)
            {
                OnDiagnostic($"Using {definition.Name}: notify={notify.Uuid}, write={write.Uuid}");
                return new ResolvedWindowsProtocol(definition.Kind, notify, write, config);
            }
        }

        await LogAllServicesAsync();
        OnInfo("BMS service discovery failed: none of the known service layouts matched.");
        return null;
    }

    private async Task LogAllServicesAsync()
    {
        if (_device is null)
        {
            return;
        }

        var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        OnDiagnostic($"All service discovery: {result.Status}, protocolError={FormatProtocolError(result.ProtocolError)}, count={result.Services.Count}");
        foreach (var service in result.Services)
        {
            OnDiagnostic($"Service {service.Uuid}");
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            OnDiagnostic($"  Characteristics: {characteristics.Status}, protocolError={FormatProtocolError(characteristics.ProtocolError)}, count={characteristics.Characteristics.Count}");
            foreach (var characteristic in characteristics.Characteristics)
            {
                OnDiagnostic($"  Characteristic {characteristic.Uuid}, properties={characteristic.CharacteristicProperties}");
            }
        }
    }

    private async Task SubscribeAsync(GattCharacteristic characteristic)
    {
        characteristic.ValueChanged += (_, args) =>
        {
            var bytes = ReadBytes(args.CharacteristicValue);
            OnDiagnostic($"Notify: {Convert.ToHexString(bytes)}");
            foreach (var rawFrame in _frameReader.Add(bytes))
            {
                if (!JbdProtocol.TryParseFrame(rawFrame, out var frame))
                {
                    OnDiagnostic($"Invalid BMS frame: {Convert.ToHexString(rawFrame)}");
                    continue;
                }

                if (_pendingCommand?.Register == frame.Register)
                {
                    _pendingCommand.Completion.TrySetResult(frame);
                }
            }

            foreach (var rawFrame in _tdtFrameReader.Add(bytes))
            {
                if (!TdtProtocol.TryParseFrame(rawFrame, out var frame))
                {
                    OnDiagnostic($"Invalid TDT frame: {Convert.ToHexString(rawFrame)}");
                    continue;
                }

                if (_pendingTdtCommand?.Command == frame.Command)
                {
                    _pendingTdtCommand.Completion.TrySetResult(frame);
                }
            }
        };

        var descriptorValue = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue);
        OnDiagnostic($"Subscribe Notify: {status}");
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        _ = args;
        var status = sender.ConnectionStatus.ToString();
        ConnectionStatusChanged?.Invoke(this, status);
        OnDiagnostic($"Connection status: {status}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _connectionLost?.TrySetResult();
        }
    }

    private async Task CleanupConnectionAsync()
    {
        await Task.CompletedTask;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
            _device.Dispose();
        }

        _notify = null;
        _write = null;
        _device = null;
        _connectionLost = null;
        _pendingCommand = null;
        _pendingTdtCommand = null;
        _config = null;
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static bool LooksLikeBatteryName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("XDZN_001", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Watt", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Cycle", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("BMS", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Battery", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("LiFe", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("LFP", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("BM-", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("XDZN", StringComparison.OrdinalIgnoreCase));

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

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            WattCycleBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            WattCycleBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string ToHexFrame(JbdFrame frame) => $"register=0x{frame.Register:X2}, status=0x{frame.Status:X2}, payload={Convert.ToHexString(frame.Payload)}";

    private static string ToHexFrame(TdtFrame frame) => $"command=0x{frame.Command:X2}, payload={Convert.ToHexString(frame.Payload)}";

    private static string FormatProtocolError(byte? protocolError) => protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "none";

    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);

    private sealed record PendingCommand(byte Register, TaskCompletionSource<JbdFrame> Completion);
    private sealed record PendingTdtCommand(byte Command, TaskCompletionSource<TdtFrame> Completion);

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
public sealed class WattCycleBtClient : IAsyncDisposable
{
    private static readonly UUID ClientCharacteristicConfigurationUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly JbdFrameReader _frameReader = new();
    private readonly TdtFrameReader _tdtFrameReader = new();
    private readonly ConcurrentDictionary<ulong, WattCycleScanCandidate> _scanCandidates = new();
    private BluetoothGatt? _gatt;
    private WattCycleGattCallback? _gattCallback;
    private BluetoothGattCharacteristic? _notify;
    private BluetoothGattCharacteristic? _write;
    private BluetoothGattCharacteristic? _config;
    private PendingCommand? _pendingCommand;
    private PendingTdtCommand? _pendingTdtCommand;
    private BmsProtocolKind _protocolKind = BmsProtocolKind.Jbd;

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

    public async Task ConnectAndPollAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
    {
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (reconnectAttempt > 0)
                {
                    OnDiagnostic($"Reconnect attempt {reconnectAttempt} to {discovered.DisplayName}...");
                }

                await ConnectAndPollOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                OnInfo($"WattCycle BT poll failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await CleanupConnectionAsync();

    private async Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        var adapter = GetBluetoothAdapter();
        var device = adapter?.GetRemoteDevice(FormatBluetoothAddress(discovered.BluetoothAddress));
        if (device is null)
        {
            OnInfo("Android could not create a BluetoothDevice for that address.");
            return;
        }

        _gattCallback = new WattCycleGattCallback(HandleCharacteristicChanged, status => ConnectionStatusChanged?.Invoke(this, status));
        _gatt = device.ConnectGatt(Application.Context, autoConnect: false, _gattCallback, BluetoothTransports.Le);
        if (_gatt is null)
        {
            OnInfo("Android ConnectGatt returned null.");
            return;
        }

        await _gattCallback.WaitForConnectedAsync(cancellationToken);
        OnDiagnostic($"Connected to {discovered.DisplayName}.");
        var serviceStatus = await _gattCallback.WaitForServicesDiscoveredAsync(cancellationToken);
        if (serviceStatus != GattStatus.Success)
        {
            OnInfo($"JBD/BMS service discovery failed: {serviceStatus}");
            return;
        }

        var protocol = FindProtocol();
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

    private async Task<WattCycleBatteryReading> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            var statusFrame = await SendTdtReadCommandAsync(TdtProtocol.StatusCommand, cancellationToken);
            var protectionFrame = await SendTdtReadCommandAsync(TdtProtocol.ProtectionCommand, cancellationToken);
            OnDiagnostic(TdtProtocol.DescribeStatusFields(statusFrame, protectionFrame));
            if (TdtProtocol.TryBuildReading(statusFrame, protectionFrame, out var reading))
            {
                return reading;
            }

            throw new InvalidOperationException($"Could not decode TDT BMS info: status={ToHexFrame(statusFrame)}, protection={ToHexFrame(protectionFrame)}");
        }

        var basicFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.BasicInfoRegister, cancellationToken);
        if (!JbdProtocol.TryParseBasicInfo(basicFrame, out var basic))
        {
            throw new InvalidOperationException($"Could not decode basic BMS info: {ToHexFrame(basicFrame)}");
        }

        var cellFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.CellVoltagesRegister, cancellationToken);
        if (!JbdProtocol.TryParseCellVoltages(cellFrame, out var cellVoltages))
        {
            OnDiagnostic($"Could not decode cell voltages: {ToHexFrame(cellFrame)}");
            cellVoltages = Array.Empty<double>();
        }

        return new WattCycleBatteryReading(
            DateTimeOffset.Now,
            basic.PackVoltage,
            basic.Current,
            basic.RemainingCapacityAh,
            basic.NominalCapacityAh,
            basic.StateOfChargePercent,
            basic.CycleCount,
            basic.ChargeMosEnabled,
            basic.DischargeMosEnabled,
            cellVoltages,
            basic.TemperaturesCelsius,
            basic.ProtectionStatus);
    }

    private async Task<TdtFrame> SendTdtReadCommandAsync(byte command, CancellationToken cancellationToken)
    {
        if (_gatt is null || _write is null || _gattCallback is null)
        {
            throw new InvalidOperationException("Write characteristic is not connected.");
        }

        foreach (var commandHead in new byte[] { 0x1e, 0x7e })
        {
            var completion = new TaskCompletionSource<TdtFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTdtCommand = new PendingTdtCommand(command, completion);
            var frame = TdtProtocol.BuildReadCommand(command, commandHead);
            _write.WriteType = _write.Properties.HasFlag(GattProperty.WriteNoResponse) ? GattWriteType.NoResponse : GattWriteType.Default;
            _write.SetValue(frame);

            var writeCompletion = _write.WriteType == GattWriteType.NoResponse ? null : _gattCallback.BeginCharacteristicWrite();
            if (!_gatt.WriteCharacteristic(_write))
            {
                _pendingTdtCommand = null;
                continue;
            }

            if (writeCompletion is not null)
            {
                var status = await writeCompletion.WaitAsync(cancellationToken);
                OnDiagnostic($"TDT read command 0x{command:X2} write ({_write.WriteType}, head=0x{commandHead:X2}): {status}, frame={Convert.ToHexString(frame)}");
                if (status != GattStatus.Success)
                {
                    _pendingTdtCommand = null;
                    continue;
                }
            }
            else
            {
                OnDiagnostic($"TDT read command 0x{command:X2} write ({_write.WriteType}, head=0x{commandHead:X2}): started, frame={Convert.ToHexString(frame)}");
            }

            try
            {
                return await completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                OnDiagnostic($"TDT read command 0x{command:X2} timed out with head=0x{commandHead:X2}.");
            }
            finally
            {
                if (_pendingTdtCommand?.Command == command)
                {
                    _pendingTdtCommand = null;
                }
            }
        }

        throw new TimeoutException($"TDT read command 0x{command:X2} timed out.");
    }

    private async Task<JbdFrame> SendReadCommandAsync(byte register, CancellationToken cancellationToken)
    {
        if (_gatt is null || _write is null || _gattCallback is null)
        {
            throw new InvalidOperationException("Write characteristic is not connected.");
        }

        var completion = new TaskCompletionSource<JbdFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommand = new PendingCommand(register, completion);
        var command = JbdProtocol.BuildReadCommand(register);
        _write.WriteType = _write.Properties.HasFlag(GattProperty.WriteNoResponse) ? GattWriteType.NoResponse : GattWriteType.Default;
        _write.SetValue(command);

        var writeCompletion = _write.WriteType == GattWriteType.NoResponse ? null : _gattCallback.BeginCharacteristicWrite();
        if (!_gatt.WriteCharacteristic(_write))
        {
            _pendingCommand = null;
            throw new InvalidOperationException($"Read register 0x{register:X2} write failed to start.");
        }

        if (writeCompletion is not null)
        {
            var status = await writeCompletion.WaitAsync(cancellationToken);
            OnDiagnostic($"Read register 0x{register:X2} write ({_write.WriteType}): {status}, frame={Convert.ToHexString(command)}");
            if (status != GattStatus.Success)
            {
                _pendingCommand = null;
                throw new InvalidOperationException($"Read register 0x{register:X2} write failed: {status}");
            }
        }
        else
        {
            OnDiagnostic($"Read register 0x{register:X2} write ({_write.WriteType}): started, frame={Convert.ToHexString(command)}");
        }

        try
        {
            return await completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            var readFrame = await TryReadNotifyCharacteristicAsync(register, cancellationToken);
            if (readFrame is not null)
            {
                return readFrame;
            }

            throw;
        }
        finally
        {
            if (_pendingCommand?.Register == register)
            {
                _pendingCommand = null;
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

    private async Task UnlockTdtAsync(CancellationToken cancellationToken)
    {
        if (_gatt is null || _gattCallback is null || _config is null)
        {
            OnDiagnostic("TDT config characteristic FFFA missing; continuing without HiLink unlock.");
            return;
        }

        _config.WriteType = _config.Properties.HasFlag(GattProperty.WriteNoResponse) ? GattWriteType.NoResponse : GattWriteType.Default;
        _config.SetValue(TdtProtocol.UnlockCommand);
        var writeCompletion = _config.WriteType == GattWriteType.NoResponse ? null : _gattCallback.BeginCharacteristicWrite();
        if (!_gatt.WriteCharacteristic(_config))
        {
            OnDiagnostic("TDT unlock write HiLink failed to start.");
            return;
        }

        if (writeCompletion is not null)
        {
            var status = await writeCompletion.WaitAsync(cancellationToken);
            OnDiagnostic($"TDT unlock write HiLink ({_config.WriteType}): {status}");
        }
        else
        {
            OnDiagnostic($"TDT unlock write HiLink ({_config.WriteType}): started");
        }

        if (_config.Properties.HasFlag(GattProperty.Read))
        {
            var readCompletion = _gattCallback.BeginCharacteristicRead();
            if (_gatt.ReadCharacteristic(_config))
            {
                var read = await readCompletion.WaitAsync(cancellationToken);
                OnDiagnostic($"TDT unlock read FFFA: {read.Status}, value={Convert.ToHexString(read.Value)}");
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

        var readResult = await readCompletion.WaitAsync(cancellationToken);
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
            return;
        }

        if (!_gatt.SetCharacteristicNotification(characteristic, true))
        {
            OnInfo("Subscribe Notify: Android refused characteristic notifications.");
            return;
        }

        var descriptor = characteristic.GetDescriptor(ClientCharacteristicConfigurationUuid);
        if (descriptor is null)
        {
            OnInfo("Subscribe Notify: CCCD missing.");
            return;
        }

        var descriptorValue = characteristic.Properties.HasFlag(GattProperty.Notify)
            ? BluetoothGattDescriptor.EnableNotificationValue
            : BluetoothGattDescriptor.EnableIndicationValue;
        descriptor.SetValue(descriptorValue?.ToArray() ?? Array.Empty<byte>());
        var descriptorCompletion = _gattCallback.BeginDescriptorWrite();
        if (!_gatt.WriteDescriptor(descriptor))
        {
            OnInfo("Subscribe Notify: descriptor write failed to start.");
            return;
        }

        var status = await descriptorCompletion.WaitAsync(cancellationToken);
        OnDiagnostic($"Subscribe Notify: {status}");
    }

    private void HandleCharacteristicChanged(BluetoothGattCharacteristic characteristic, byte[] bytes)
    {
        _ = characteristic;
        OnDiagnostic($"Notify: {Convert.ToHexString(bytes)}");
        foreach (var rawFrame in _frameReader.Add(bytes))
        {
            if (!JbdProtocol.TryParseFrame(rawFrame, out var frame))
            {
                OnDiagnostic($"Invalid BMS frame: {Convert.ToHexString(rawFrame)}");
                continue;
            }

            if (_pendingCommand?.Register == frame.Register)
            {
                _pendingCommand.Completion.TrySetResult(frame);
            }
        }

        foreach (var rawFrame in _tdtFrameReader.Add(bytes))
        {
            if (!TdtProtocol.TryParseFrame(rawFrame, out var frame))
            {
                OnDiagnostic($"Invalid TDT frame: {Convert.ToHexString(rawFrame)}");
                continue;
            }

            if (_pendingTdtCommand?.Command == frame.Command)
            {
                _pendingTdtCommand.Completion.TrySetResult(frame);
            }
        }
    }

    private async Task CleanupConnectionAsync()
    {
        await Task.CompletedTask;
        _notify = null;
        _write = null;
        _config = null;
        _gatt?.Disconnect();
        _gatt?.Close();
        _gatt?.Dispose();
        _gatt = null;
        _gattCallback?.Dispose();
        _gattCallback = null;
        _pendingCommand = null;
        _pendingTdtCommand = null;
    }

    private static BluetoothAdapter? GetBluetoothAdapter()
    {
        var manager = (BluetoothManager?)Application.Context.GetSystemService(Context.BluetoothService);
        return manager?.Adapter;
    }

    private static bool LooksLikeBatteryName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("XDZN_001", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Watt", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Cycle", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("BMS", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Battery", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("LiFe", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("LFP", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("BM-", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("XDZN", StringComparison.OrdinalIgnoreCase));

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

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            WattCycleBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            WattCycleBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string ToHexFrame(JbdFrame frame) => $"register=0x{frame.Register:X2}, status=0x{frame.Status:X2}, payload={Convert.ToHexString(frame.Payload)}";

    private static string ToHexFrame(TdtFrame frame) => $"command=0x{frame.Command:X2}, payload={Convert.ToHexString(frame.Payload)}";

    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);

    private sealed record PendingCommand(byte Register, TaskCompletionSource<JbdFrame> Completion);
    private sealed record PendingTdtCommand(byte Command, TaskCompletionSource<TdtFrame> Completion);

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
        private readonly Action<BluetoothGattCharacteristic, byte[]> _onCharacteristicChanged;
        private readonly Action<string> _onConnectionStatusChanged;
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GattStatus> _servicesDiscovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<GattStatus>? _descriptorWrite;
        private TaskCompletionSource<GattStatus>? _characteristicWrite;
        private TaskCompletionSource<CharacteristicReadResult>? _characteristicRead;

        public WattCycleGattCallback(Action<BluetoothGattCharacteristic, byte[]> onCharacteristicChanged, Action<string> onConnectionStatusChanged)
        {
            _onCharacteristicChanged = onCharacteristicChanged;
            _onConnectionStatusChanged = onConnectionStatusChanged;
        }

        public Task WaitForConnectedAsync(CancellationToken cancellationToken) => _connected.Task.WaitAsync(cancellationToken);

        public Task<GattStatus> WaitForServicesDiscoveredAsync(CancellationToken cancellationToken) => _servicesDiscovered.Task.WaitAsync(cancellationToken);

        public Task DisconnectedTask => _disconnected.Task;

        public Task<GattStatus> BeginDescriptorWrite()
        {
            _descriptorWrite = new TaskCompletionSource<GattStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _descriptorWrite.Task;
        }

        public Task<GattStatus> BeginCharacteristicWrite()
        {
            _characteristicWrite = new TaskCompletionSource<GattStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _characteristicWrite.Task;
        }

        public Task<CharacteristicReadResult> BeginCharacteristicRead()
        {
            _characteristicRead = new TaskCompletionSource<CharacteristicReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _characteristicRead.Task;
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            _onConnectionStatusChanged(newState.ToString());
            if (status != GattStatus.Success)
            {
                _connected.TrySetException(new InvalidOperationException($"Android GATT connection failed: {status}"));
                _disconnected.TrySetResult();
                return;
            }

            if (newState == ProfileState.Connected)
            {
                _connected.TrySetResult();
                gatt?.DiscoverServices();
            }
            else if (newState == ProfileState.Disconnected)
            {
                _disconnected.TrySetResult();
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            _ = gatt;
            _servicesDiscovered.TrySetResult(status);
        }

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            _ = gatt;
            if (characteristic?.GetValue() is { } value)
            {
                _onCharacteristicChanged(characteristic, value);
            }
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value)
        {
            _ = gatt;
            _onCharacteristicChanged(characteristic, value);
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            _ = gatt;
            _ = descriptor;
            _descriptorWrite?.TrySetResult(status);
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            _ = gatt;
            _ = characteristic;
            _characteristicWrite?.TrySetResult(status);
        }

        public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            _ = gatt;
            _characteristicRead?.TrySetResult(new CharacteristicReadResult(status, characteristic?.GetValue() ?? Array.Empty<byte>()));
        }

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, GattStatus status)
        {
            _ = gatt;
            _ = characteristic;
            _characteristicRead?.TrySetResult(new CharacteristicReadResult(status, value));
        }
    }

    private sealed record CharacteristicReadResult(GattStatus Status, byte[] Value);
}
#pragma warning restore CA1416, CA1422
#else
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
        InfoMessage?.Invoke(this, "WattCycle Bluetooth is currently implemented for Windows and Android only.");
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
        InfoMessage?.Invoke(this, "WattCycle Bluetooth scanning is currently implemented for Windows and Android only.");
        return Task.FromResult<IReadOnlyList<WattCycleDeviceAdvertisement>>(Array.Empty<WattCycleDeviceAdvertisement>());
    }

    public Task ConnectAndPollAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
    {
        _ = discovered;
        _ = cancellationToken;
        InfoMessage?.Invoke(this, "WattCycle Bluetooth polling is currently implemented for Windows and Android only.");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#pragma warning restore CS0067
#endif
