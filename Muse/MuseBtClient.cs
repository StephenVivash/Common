#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Muse.Core;

public sealed class MuseBtClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, long> _notificationCounts = new();
    private readonly ConcurrentDictionary<Guid, EegBandPowerTracker> _eegBandTrackers = new();
    private readonly ConcurrentDictionary<Guid, MuseEegPacketStats> _eegPacketStats = new();
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _control;
    private TaskCompletionSource? _connectionLost;

    public event EventHandler<string>? DiagnosticMessage;

    public event EventHandler<string>? InfoMessage;

    public event EventHandler<string>? ConnectionStatusChanged;

    public event EventHandler<MuseNotification>? NotificationReceived;

    public event EventHandler<MuseEegPacketDiagnostic>? EegPacketDiagnostic;

    public event EventHandler<MuseBandPowerDiagnostic>? BandPowerDiagnostic;

    public event EventHandler<MuseBandPowerReading>? BandPowersCalculated;

    public async Task<MuseDeviceAdvertisement?> FindMuseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<MuseDeviceAdvertisement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName;
            var advertisesMuseService = args.Advertisement.ServiceUuids.Any(uuid => uuid == MuseBluetoothConstants.MuseServiceUuid);
            var looksLikeMuse = advertisesMuseService || name.Contains("Muse", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeMuse)
            {
                return;
            }

            var discovered = new MuseDeviceAdvertisement(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, advertisesMuseService);
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

        await using var _ = timeoutCts.Token.Register(() => completion.TrySetResult(null));

        OnDiagnostic("Scanning for Muse BLE advertisements...");
        watcher.Start();

        try
        {
            return await completion.Task;
        }
        finally
        {
            watcher.Stop();
        }
    }

    public async Task ConnectAndStreamAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
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

                await ConnectAndStreamOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                OnInfo($"Muse BT stream failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync(sendStopCommand: false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task ConnectAndStreamOnceAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken)
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

        var servicesResult = await _device.GetGattServicesForUuidAsync(MuseBluetoothConstants.MuseServiceUuid, BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            OnInfo($"Muse service discovery failed: {servicesResult.Status}, protocolError={FormatProtocolError(servicesResult.ProtocolError)}");
            return;
        }

        var service = servicesResult.Services[0];
        OnDiagnostic($"Muse service discovered: {service.Uuid}");

        _control = await FindCharacteristicAsync(service, MuseBluetoothConstants.ControlUuid, "Control");
        if (_control is null)
        {
            return;
        }

        await SubscribeAsync(_control, "Control", MuseSensorKind.Control);

        foreach (var sensor in MuseBluetoothConstants.Sensors)
        {
            var characteristic = await FindCharacteristicAsync(service, sensor.Uuid, sensor.Name, optional: true);
            if (characteristic is not null)
            {
                await SubscribeAsync(characteristic, sensor.Name, sensor.Kind);
            }
        }

        await StartClassicMuseStreamAsync(cancellationToken);
        await RunStreamingLoopAsync(cancellationToken);
    }

    public async Task SendControlCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_control is null)
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException($"Control command too long: {command}");
        }

        var payload = new byte[bytes.Length + 1];
        payload[0] = (byte)bytes.Length;
        bytes.CopyTo(payload, 1);

        var writeOption = _control.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(payload);
            var status = await _control.WriteValueAsync(writer.DetachBuffer(), writeOption).AsTask(cancellationToken);
            OnDiagnostic($"Control write '{command}' ({writeOption}): {status}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            OnInfo($"Control write '{command}' ({writeOption}) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupConnectionAsync(sendStopCommand: true);
    }

    private async Task RunStreamingLoopAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Streaming.");
        try
        {
            var keepAliveTimer = Stopwatch.StartNew();
            var lastNotificationTotal = TotalNotifications();
            var lastNotificationProgress = DateTimeOffset.UtcNow;
            var connectionLostTask = _connectionLost?.Task ?? Task.Delay(Timeout.InfiniteTimeSpan);
            while (!cancellationToken.IsCancellationRequested)
            {
                var delayTask = Task.Delay(MuseBluetoothConstants.BandPowerPublishInterval, cancellationToken);
                if (await Task.WhenAny(delayTask, connectionLostTask) == connectionLostTask)
                {
                    OnDiagnostic("Connection lost; leaving stream loop for reconnect.");
                    break;
                }

                await delayTask;
                PublishBandSummaries();

                var notificationTotal = TotalNotifications();
                if (notificationTotal > lastNotificationTotal)
                {
                    lastNotificationTotal = notificationTotal;
                    lastNotificationProgress = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - lastNotificationProgress >= MuseBluetoothConstants.NotificationStallReconnectTimeout)
                {
                    OnInfo($"No Muse notifications for {MuseBluetoothConstants.NotificationStallReconnectTimeout.TotalSeconds:F0}s; reconnecting.");
                    _connectionLost?.TrySetResult();
                    continue;
                }

                if (keepAliveTimer.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    keepAliveTimer.Restart();
                    var summary = string.Join(", ", _notificationCounts.Select(pair => $"{ShortUuid(pair.Key)}={pair.Value}"));
                    OnDiagnostic($"Heartbeat: status={_device?.ConnectionStatus}, notifications=[{summary}]");
                    await SendControlCommandAsync("k", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal caller-requested shutdown path.
        }
        finally
        {
            var connected = _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
            OnDiagnostic(connected ? "Stopping stream..." : "Stream stopped; device is disconnected.");
            if (connected)
            {
                await SendControlCommandAsync("h", CancellationToken.None);
            }
        }
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

    private async Task CleanupConnectionAsync(bool sendStopCommand)
    {
        if (sendStopCommand && _control is not null && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            try
            {
                await SendControlCommandAsync("h", CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                OnDiagnostic($"Stop command during cleanup failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
            _device.Dispose();
        }

        _control = null;
        _device = null;
        _connectionLost = null;
    }

    private long TotalNotifications() => _notificationCounts.Values.Sum();

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            MuseBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            MuseBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
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

    private async Task SubscribeAsync(GattCharacteristic characteristic, string name, MuseSensorKind kind)
    {
        characteristic.ValueChanged += (_, args) =>
        {
            var bytes = ReadBytes(args.CharacteristicValue);
            var count = _notificationCounts.AddOrUpdate(characteristic.Uuid, 1, (_, value) => value + 1);

            if (kind == MuseSensorKind.Eeg && MusePacketDecoder.TryDecodeEeg(bytes, out var packet))
            {
                var tracker = _eegBandTrackers.GetOrAdd(
                    characteristic.Uuid,
                    _ => new EegBandPowerTracker(
                        name,
                        MuseBluetoothConstants.DefaultBandWindowSamples,
                        MuseBluetoothConstants.EegSampleRate,
                        MuseBluetoothConstants.DefaultBandHopSamples));
                tracker.AddSamples(packet.Samples);

                var stats = _eegPacketStats.GetOrAdd(characteristic.Uuid, _ => new MuseEegPacketStats());
                EegPacketDiagnostic?.Invoke(this, stats.Track(name, count, packet));
            }

            NotificationReceived?.Invoke(this, new MuseNotification(name, characteristic.Uuid, kind, bytes, count));
        };

        var descriptorValue = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue);
        OnDiagnostic($"Subscribe {name}: {status}");
    }

    private async Task StartClassicMuseStreamAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Sending Muse classic start sequence: h, s, p21, d.");
        await SendControlCommandAsync("h", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("s", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("p21", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("d", cancellationToken);
    }

    private void PublishBandSummaries()
    {
        foreach (var tracker in _eegBandTrackers.Values.OrderBy(tracker => tracker.Name))
        {
            if (tracker.TryCalculate(out var bands, out var diagnostic))
            {
                if (diagnostic is not null)
                {
                    BandPowerDiagnostic?.Invoke(this, diagnostic);
                    OnDiagnostic(FormatBandPowerDiagnostic(diagnostic));
                }

                BandPowersCalculated?.Invoke(this, new MuseBandPowerReading(tracker.Name, bands));
            }
        }
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string ShortUuid(Guid uuid) => uuid.ToString()[4..8];

    private static string FormatProtocolError(byte? protocolError) => protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "none";

    private static string FormatBandPowerDiagnostic(MuseBandPowerDiagnostic diagnostic) =>
        $"PSD {ShortSensorName(diagnostic.SensorName),-4} #{diagnostic.Count,5} " +
        $"n={diagnostic.WindowSamples} hop={diagnostic.HopSamples} fs={diagnostic.SampleRate} notch={FormatNotch(diagnostic)} " +
        $"uV[min={diagnostic.MinMicrovolts,7:F1}, max={diagnostic.MaxMicrovolts,7:F1}, mean={diagnostic.MeanMicrovolts,7:F2}, " +
        $"meanAbs={diagnostic.MeanAbsMicrovolts,6:F1}, rms={diagnostic.RmsMicrovolts,6:F1}] " +
        $"gamma[pow={diagnostic.GammaIntegratedPower:E3}, final={diagnostic.GammaLogPower,6:F3}, dB={diagnostic.GammaDb,6:F2}, " +
        $"peak={diagnostic.GammaPeakHz,5:F1}Hz/{diagnostic.GammaPeakPower:E3}, spike={diagnostic.GammaSpikeLimit:E3}/x{diagnostic.GammaSpikeLimitMultiplier:F1}] " +
        $"highPeak={diagnostic.HighPeakHz,5:F1}Hz/{diagnostic.HighPeakPower:E3} line50={diagnostic.Line50HzPower:E3} line60={diagnostic.Line60HzPower:E3}";

    private static string FormatNotch(MuseBandPowerDiagnostic diagnostic) =>
        diagnostic.NotchFrequencyHz > 0.0 ? $"{diagnostic.NotchFrequencyHz:F1}Hz/q{diagnostic.NotchQ:F1}" : "off";

    private static string ShortSensorName(string name) => name.StartsWith("EEG ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);
}
#elif ANDROID
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;

namespace Muse.Core;

#pragma warning disable CA1416, CA1422
public sealed class MuseBtClient : IAsyncDisposable
{
    private static readonly UUID ClientCharacteristicConfigurationUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly ConcurrentDictionary<Guid, long> _notificationCounts = new();
    private readonly ConcurrentDictionary<Guid, EegBandPowerTracker> _eegBandTrackers = new();
    private readonly ConcurrentDictionary<Guid, MuseEegPacketStats> _eegPacketStats = new();
    private BluetoothGatt? _gatt;
    private MuseGattCallback? _gattCallback;
    private BluetoothGattCharacteristic? _control;

    public event EventHandler<string>? DiagnosticMessage;

    public event EventHandler<string>? InfoMessage;

    public event EventHandler<string>? ConnectionStatusChanged;

    public event EventHandler<MuseNotification>? NotificationReceived;

    public event EventHandler<MuseEegPacketDiagnostic>? EegPacketDiagnostic;

    public event EventHandler<MuseBandPowerDiagnostic>? BandPowerDiagnostic;

    public event EventHandler<MuseBandPowerReading>? BandPowersCalculated;

    public async Task<MuseDeviceAdvertisement?> FindMuseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var adapter = GetBluetoothAdapter();
        if (adapter is null)
        {
            OnInfo("Android Bluetooth adapter is not available.");
            return null;
        }

        OnDiagnostic($"Android BLE adapter state={adapter.State}, enabled={adapter.IsEnabled}");
        var scanner = adapter.BluetoothLeScanner;
        if (scanner is null)
        {
            OnInfo("Android Bluetooth LE scanner is not available.");
            return null;
        }

        var completion = new TaskCompletionSource<MuseDeviceAdvertisement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var callback = new MuseScanCallback(
            discovered =>
            {
                OnDiagnostic($"Found candidate: {discovered.DisplayName}, address=0x{discovered.BluetoothAddress:X}, rssi={discovered.Rssi} dBm, serviceAdvertised={discovered.ServiceAdvertised}");
                completion.TrySetResult(discovered);
            },
            errorCode =>
            {
                OnDiagnostic($"Android BLE scan failed: errorCode={errorCode}");
                completion.TrySetResult(null);
            },
            message => OnDiagnostic(message));

        await using var _ = timeoutCts.Token.Register(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                OnDiagnostic("Android BLE scan timed out with no Muse advertisement.");
            }

            completion.TrySetResult(null);
        });

        OnDiagnostic("Scanning for Muse BLE advertisements...");
        var settingsBuilder = new ScanSettings.Builder();
        settingsBuilder.SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency);
        var settings = settingsBuilder.Build();
        scanner.StartScan(new List<ScanFilter>(), settings, callback);
        try
        {
            return await completion.Task;
        }
        finally
        {
            scanner.StopScan(callback);
            callback.Dispose();
        }
    }

    public async Task ConnectAndStreamAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
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

                await ConnectAndStreamOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                OnInfo($"Muse BT stream failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync(sendStopCommand: false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task ConnectAndStreamOnceAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken)
    {
        var adapter = GetBluetoothAdapter();
        if (adapter is null)
        {
            OnInfo("Android Bluetooth adapter is not available.");
            return;
        }

        var address = FormatBluetoothAddress(discovered.BluetoothAddress);
        var device = adapter.GetRemoteDevice(address);
        if (device is null)
        {
            OnInfo($"Android could not create a BluetoothDevice for {address}.");
            return;
        }

        _gattCallback = new MuseGattCallback(HandleCharacteristicChanged, status =>
        {
            ConnectionStatusChanged?.Invoke(this, status);
            OnDiagnostic($"Connection status: {status}");
        });

        OnDiagnostic($"Connecting to {discovered.DisplayName} ({address})...");
        _gatt = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? device.ConnectGatt(Application.Context, false, _gattCallback, BluetoothTransports.Le)
            : device.ConnectGatt(Application.Context, false, _gattCallback);

        if (_gatt is null)
        {
            OnInfo("Android could not create a BluetoothGatt connection.");
            return;
        }

        await _gattCallback.WaitForConnectedAsync(cancellationToken);
        var serviceDiscoveryStatus = await _gattCallback.WaitForServicesDiscoveredAsync(cancellationToken);
        if (serviceDiscoveryStatus != GattStatus.Success)
        {
            OnInfo($"Muse service discovery failed: {serviceDiscoveryStatus}");
            return;
        }

        var service = _gatt.GetService(ToJavaUuid(MuseBluetoothConstants.MuseServiceUuid));
        if (service is null)
        {
            OnInfo("Muse service discovery failed: service missing.");
            return;
        }

        OnDiagnostic($"Muse service discovered: {service.Uuid}");

        _control = FindCharacteristic(service, MuseBluetoothConstants.ControlUuid, "Control");
        if (_control is null)
        {
            return;
        }

        await SubscribeAsync(_control, "Control", MuseSensorKind.Control, cancellationToken);

        foreach (var sensor in MuseBluetoothConstants.Sensors)
        {
            var characteristic = FindCharacteristic(service, sensor.Uuid, sensor.Name, optional: true);
            if (characteristic is not null)
            {
                await SubscribeAsync(characteristic, sensor.Name, sensor.Kind, cancellationToken);
            }
        }

        await StartClassicMuseStreamAsync(cancellationToken);
        await RunStreamingLoopAsync(cancellationToken);
    }

    public async Task SendControlCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_gatt is null || _control is null || _gattCallback is null)
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException($"Control command too long: {command}");
        }

        var payload = new byte[bytes.Length + 1];
        payload[0] = (byte)bytes.Length;
        bytes.CopyTo(payload, 1);

        _control.WriteType = _control.Properties.HasFlag(GattProperty.Write)
            ? GattWriteType.Default
            : GattWriteType.NoResponse;
        _control.SetValue(payload);

        var writeCompletion = _gattCallback.BeginCharacteristicWrite();
        if (!_gatt.WriteCharacteristic(_control))
        {
            OnInfo($"Control write '{command}' failed to start.");
            return;
        }

        var status = await writeCompletion.WaitAsync(cancellationToken);
        OnDiagnostic($"Control write '{command}' ({_control.WriteType}): {status}");
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupConnectionAsync(sendStopCommand: true);
    }

    private async Task RunStreamingLoopAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Streaming.");
        try
        {
            var keepAliveTimer = Stopwatch.StartNew();
            var lastNotificationTotal = TotalNotifications();
            var lastNotificationProgress = DateTimeOffset.UtcNow;
            var disconnectedTask = _gattCallback?.DisconnectedTask ?? Task.Delay(Timeout.InfiniteTimeSpan);
            while (!cancellationToken.IsCancellationRequested)
            {
                var delayTask = Task.Delay(MuseBluetoothConstants.BandPowerPublishInterval, cancellationToken);
                if (await Task.WhenAny(delayTask, disconnectedTask) == disconnectedTask)
                {
                    OnDiagnostic("Connection lost; leaving stream loop for reconnect.");
                    break;
                }

                await delayTask;
                PublishBandSummaries();

                var notificationTotal = TotalNotifications();
                if (notificationTotal > lastNotificationTotal)
                {
                    lastNotificationTotal = notificationTotal;
                    lastNotificationProgress = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - lastNotificationProgress >= MuseBluetoothConstants.NotificationStallReconnectTimeout)
                {
                    OnInfo($"No Muse notifications for {MuseBluetoothConstants.NotificationStallReconnectTimeout.TotalSeconds:F0}s; reconnecting.");
                    _gattCallback?.SignalReconnectRequested();
                    continue;
                }

                if (keepAliveTimer.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    keepAliveTimer.Restart();
                    var summary = string.Join(", ", _notificationCounts.Select(pair => $"{ShortUuid(pair.Key)}={pair.Value}"));
                    OnDiagnostic($"Heartbeat: notifications=[{summary}]");
                    await SendControlCommandAsync("k", cancellationToken);
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            // Normal caller-requested shutdown path.
        }
        finally
        {
            OnDiagnostic("Stopping stream...");
            await SendControlCommandAsync("h", CancellationToken.None);
        }
    }

    private async Task CleanupConnectionAsync(bool sendStopCommand)
    {
        if (sendStopCommand && _control is not null && _gatt is not null)
        {
            try
            {
                await SendControlCommandAsync("h", CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                OnDiagnostic($"Stop command during cleanup failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        _control = null;
        _gatt?.Disconnect();
        _gatt?.Close();
        _gatt?.Dispose();
        _gatt = null;
        _gattCallback?.Dispose();
        _gattCallback = null;
    }

    private long TotalNotifications() => _notificationCounts.Values.Sum();

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            MuseBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            MuseBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
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

    private async Task SubscribeAsync(BluetoothGattCharacteristic characteristic, string name, MuseSensorKind kind, CancellationToken cancellationToken)
    {
        if (_gatt is null || _gattCallback is null)
        {
            return;
        }

        _gattCallback.RegisterCharacteristic(characteristic.Uuid!, name, kind);
        if (!_gatt.SetCharacteristicNotification(characteristic, true))
        {
            OnInfo($"Subscribe {name}: Android refused characteristic notifications.");
            return;
        }

        var descriptor = characteristic.GetDescriptor(ClientCharacteristicConfigurationUuid);
        if (descriptor is null)
        {
            OnInfo($"Subscribe {name}: CCCD missing.");
            return;
        }

        var descriptorValue = characteristic.Properties.HasFlag(GattProperty.Notify)
            ? BluetoothGattDescriptor.EnableNotificationValue
            : BluetoothGattDescriptor.EnableIndicationValue;

        descriptor.SetValue(descriptorValue?.ToArray() ?? Array.Empty<byte>());
        var descriptorCompletion = _gattCallback.BeginDescriptorWrite();
        if (!_gatt.WriteDescriptor(descriptor))
        {
            OnInfo($"Subscribe {name}: descriptor write failed to start.");
            return;
        }

        var status = await descriptorCompletion.WaitAsync(cancellationToken);
        OnDiagnostic($"Subscribe {name}: {status}");
    }

    private async Task StartClassicMuseStreamAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Sending Muse classic start sequence: h, s, p21, d.");
        await SendControlCommandAsync("h", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("s", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("p21", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("d", cancellationToken);
    }

    private void HandleCharacteristicChanged(BluetoothGattCharacteristic characteristic, byte[] bytes)
    {
        if (_gattCallback is null || !_gattCallback.TryGetCharacteristicInfo(characteristic.Uuid!, out var info))
        {
            return;
        }

        var uuid = FromJavaUuid(characteristic.Uuid!);
        var count = _notificationCounts.AddOrUpdate(uuid, 1, (_, value) => value + 1);

        if (info.Kind == MuseSensorKind.Eeg && MusePacketDecoder.TryDecodeEeg(bytes, out var packet))
        {
            var tracker = _eegBandTrackers.GetOrAdd(
                uuid,
                _ => new EegBandPowerTracker(
                    info.Name,
                    MuseBluetoothConstants.DefaultBandWindowSamples,
                    MuseBluetoothConstants.EegSampleRate,
                    MuseBluetoothConstants.DefaultBandHopSamples));
            tracker.AddSamples(packet.Samples);

            var stats = _eegPacketStats.GetOrAdd(uuid, _ => new MuseEegPacketStats());
            EegPacketDiagnostic?.Invoke(this, stats.Track(info.Name, count, packet));
        }

        NotificationReceived?.Invoke(this, new MuseNotification(info.Name, uuid, info.Kind, bytes, count));
    }

    private void PublishBandSummaries()
    {
        foreach (var tracker in _eegBandTrackers.Values.OrderBy(tracker => tracker.Name))
        {
            if (tracker.TryCalculate(out var bands, out var diagnostic))
            {
                if (diagnostic is not null)
                {
                    BandPowerDiagnostic?.Invoke(this, diagnostic);
                    OnDiagnostic(FormatBandPowerDiagnostic(diagnostic));
                }

                BandPowersCalculated?.Invoke(this, new MuseBandPowerReading(tracker.Name, bands));
            }
        }
    }

    private static BluetoothAdapter? GetBluetoothAdapter()
    {
        var manager = (BluetoothManager?)Application.Context.GetSystemService(Context.BluetoothService);
        return manager?.Adapter;
    }

    private static UUID ToJavaUuid(Guid guid) => UUID.FromString(guid.ToString())!;

    private static Guid FromJavaUuid(UUID uuid) => Guid.Parse(uuid.ToString());

    private static ulong ParseBluetoothAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return 0;
        }

        return Convert.ToUInt64(address.Replace(":", "", StringComparison.Ordinal), 16);
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static string ShortUuid(Guid uuid) => uuid.ToString()[4..8];

    private static string FormatBandPowerDiagnostic(MuseBandPowerDiagnostic diagnostic) =>
        $"PSD {ShortSensorName(diagnostic.SensorName),-4} #{diagnostic.Count,5} " +
        $"n={diagnostic.WindowSamples} hop={diagnostic.HopSamples} fs={diagnostic.SampleRate} notch={FormatNotch(diagnostic)} " +
        $"uV[min={diagnostic.MinMicrovolts,7:F1}, max={diagnostic.MaxMicrovolts,7:F1}, mean={diagnostic.MeanMicrovolts,7:F2}, " +
        $"meanAbs={diagnostic.MeanAbsMicrovolts,6:F1}, rms={diagnostic.RmsMicrovolts,6:F1}] " +
        $"gamma[pow={diagnostic.GammaIntegratedPower:E3}, final={diagnostic.GammaLogPower,6:F3}, dB={diagnostic.GammaDb,6:F2}, " +
        $"peak={diagnostic.GammaPeakHz,5:F1}Hz/{diagnostic.GammaPeakPower:E3}, spike={diagnostic.GammaSpikeLimit:E3}/x{diagnostic.GammaSpikeLimitMultiplier:F1}] " +
        $"highPeak={diagnostic.HighPeakHz,5:F1}Hz/{diagnostic.HighPeakPower:E3} line50={diagnostic.Line50HzPower:E3} line60={diagnostic.Line60HzPower:E3}";

    private static string FormatNotch(MuseBandPowerDiagnostic diagnostic) =>
        diagnostic.NotchFrequencyHz > 0.0 ? $"{diagnostic.NotchFrequencyHz:F1}Hz/q{diagnostic.NotchQ:F1}" : "off";

    private static string ShortSensorName(string name) => name.StartsWith("EEG ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);

    private sealed class MuseScanCallback : ScanCallback
    {
        private readonly Action<MuseDeviceAdvertisement> _onDiscovered;
        private readonly Action<ScanFailure>? _onScanFailed;
        private readonly Action<string>? _onDiagnostic;
        private int _resultsSeen;
        private int _ignoredLogged;

        public MuseScanCallback(
            Action<MuseDeviceAdvertisement> onDiscovered,
            Action<ScanFailure>? onScanFailed,
            Action<string>? onDiagnostic)
        {
            _onDiscovered = onDiscovered;
            _onScanFailed = onScanFailed;
            _onDiagnostic = onDiagnostic;
        }

        public override void OnScanFailed(ScanFailure errorCode)
        {
            _onScanFailed?.Invoke(errorCode);
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
            var advertisesMuseService = record?.ServiceUuids?.Any(uuid => uuid?.Uuid is not null && FromJavaUuid(uuid.Uuid).Equals(MuseBluetoothConstants.MuseServiceUuid)) == true;
            var looksLikeMuse = advertisesMuseService || name.Contains("Muse", StringComparison.OrdinalIgnoreCase);
            _resultsSeen++;
            if (!looksLikeMuse)
            {
                if (_ignoredLogged < 12)
                {
                    var serviceUuids = record?.ServiceUuids is null
                        ? ""
                        : string.Join(",", record.ServiceUuids.Select(uuid => uuid?.Uuid?.ToString()).Where(uuid => !string.IsNullOrWhiteSpace(uuid)));
                    var displayName = string.IsNullOrWhiteSpace(name) ? "(no name)" : name;
                    _onDiagnostic?.Invoke($"Ignored BLE advertisement #{_resultsSeen}: name='{displayName}', address={device.Address}, rssi={result.Rssi} dBm, services=[{serviceUuids}]");
                    _ignoredLogged++;
                }

                return;
            }

            _onDiscovered(new MuseDeviceAdvertisement(ParseBluetoothAddress(device.Address), name, (short)result.Rssi, advertisesMuseService));
        }
    }

    private sealed class MuseGattCallback : BluetoothGattCallback
    {
        private readonly ConcurrentDictionary<string, CharacteristicInfo> _characteristics = new();
        private readonly Action<BluetoothGattCharacteristic, byte[]> _onCharacteristicChanged;
        private readonly Action<string> _onConnectionStatusChanged;
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GattStatus> _servicesDiscovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<GattStatus>? _descriptorWrite;
        private TaskCompletionSource<GattStatus>? _characteristicWrite;

        public MuseGattCallback(Action<BluetoothGattCharacteristic, byte[]> onCharacteristicChanged, Action<string> onConnectionStatusChanged)
        {
            _onCharacteristicChanged = onCharacteristicChanged;
            _onConnectionStatusChanged = onConnectionStatusChanged;
        }

        public Task WaitForConnectedAsync(CancellationToken cancellationToken) => _connected.Task.WaitAsync(cancellationToken);

        public Task<GattStatus> WaitForServicesDiscoveredAsync(CancellationToken cancellationToken) => _servicesDiscovered.Task.WaitAsync(cancellationToken);

        public Task DisconnectedTask => _disconnected.Task;

        public void SignalReconnectRequested() => _disconnected.TrySetResult();

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

        public void RegisterCharacteristic(UUID uuid, string name, MuseSensorKind kind)
        {
            _characteristics[uuid.ToString()] = new CharacteristicInfo(name, kind);
        }

        public bool TryGetCharacteristicInfo(UUID uuid, out CharacteristicInfo info) => _characteristics.TryGetValue(uuid.ToString(), out info!);

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
    }

    private readonly record struct CharacteristicInfo(string Name, MuseSensorKind Kind);
}
#pragma warning restore CA1416, CA1422
#else
namespace Muse.Core;

#pragma warning disable CS0067
public sealed class MuseBtClient : IAsyncDisposable
{
	public event EventHandler<string>? DiagnosticMessage;

	public event EventHandler<string>? InfoMessage;

	public event EventHandler<string>? ConnectionStatusChanged;

	public event EventHandler<MuseNotification>? NotificationReceived;

	public event EventHandler<MuseEegPacketDiagnostic>? EegPacketDiagnostic;

	public event EventHandler<MuseBandPowerDiagnostic>? BandPowerDiagnostic;

	public event EventHandler<MuseBandPowerReading>? BandPowersCalculated;

	public Task<MuseDeviceAdvertisement?> FindMuseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		_ = timeout;
		InfoMessage?.Invoke(this, "Muse Bluetooth is currently implemented for Windows and Android only.");
		return Task.FromResult<MuseDeviceAdvertisement?>(null);
	}

	public Task ConnectAndStreamAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
	{
		_ = discovered;
		_ = cancellationToken;
		InfoMessage?.Invoke(this, "Muse Bluetooth streaming is currently implemented for Windows and Android only.");
		return Task.CompletedTask;
	}

	public Task SendControlCommandAsync(string command, CancellationToken cancellationToken = default)
	{
		_ = command;
		_ = cancellationToken;
		DiagnosticMessage?.Invoke(this, "Ignoring control command because Muse Bluetooth is not available on this platform.");
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#pragma warning restore CS0067
#endif
