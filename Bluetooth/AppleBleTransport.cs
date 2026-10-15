#if IOS || MACCATALYST
using CoreBluetooth;
using CoreFoundation;
using Foundation;

namespace Common.Bluetooth;

public sealed record AppleBleAdvertisement(Guid Identifier, string Name, short Rssi, IReadOnlyList<Guid> Services);

// Protocol-independent CoreBluetooth transport for iOS and Mac Catalyst. Each
// instance owns one central and one peripheral session; failed sessions are discarded.
public sealed class AppleBleTransport : IAsyncDisposable
{
    private readonly CentralDelegate _centralDelegate;
    private readonly PeripheralDelegate _peripheralDelegate;
    private CBCentralManager? _central;
    private CBPeripheral? _peripheral;
    private TaskCompletionSource? _stateChanged;
    private readonly Dictionary<string, TaskCompletionSource> _operations = new();
    private Action<AppleBleAdvertisement>? _discovered;
    private Exception? _failure;
    private bool _closing;
    private readonly TaskCompletionSource _disconnected = NewCompletion();
    private Task? _disposeTask;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    public event Action<Guid, byte[]>? Notification;
    public event Action<Exception>? ConnectionFailed;
    public Task Disconnected => _disconnected.Task;

    public AppleBleTransport()
    {
        _centralDelegate = new(this);
        _peripheralDelegate = new(this);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task OnQueue(Action action)
    {
        var completion = NewCompletion();
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private void ThrowIfUnavailable()
    {
        if (_closing) throw new ObjectDisposedException(nameof(AppleBleTransport));
        if (_failure is not null) throw new IOException("Bluetooth session is no longer usable.", _failure);
    }

    private async Task ReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task? changed = null;
            var ready = false;
            await OnQueue(() =>
            {
                ThrowIfUnavailable();
                _central ??= new CBCentralManager(_centralDelegate, DispatchQueue.MainQueue);
                switch (_central.State)
                {
                    case CBManagerState.PoweredOn: ready = true; break;
                    case CBManagerState.Unauthorized: throw new UnauthorizedAccessException("Bluetooth permission denied. Enable Bluetooth access in Settings.");
                    case CBManagerState.PoweredOff: throw new IOException("Bluetooth is turned off.");
                    case CBManagerState.Unsupported: throw new PlatformNotSupportedException("Bluetooth LE is unavailable on this device.");
                    default: _stateChanged ??= NewCompletion(); changed = _stateChanged.Task; break;
                }
            });
            if (ready) return;
            await changed!.WaitAsync(OperationTimeout, cancellationToken);
        }
    }

    public async Task ScanAsync(TimeSpan duration, Action<AppleBleAdvertisement> discovered, CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken);
        await OnQueue(() =>
        {
            ThrowIfUnavailable();
            if (_discovered is not null) throw new InvalidOperationException("A scan is already active.");
            _discovered = discovered;
            // Name-only advertisements must remain visible.
            _central!.ScanForPeripherals((CBUUID[]?)null, new PeripheralScanningOptions { AllowDuplicatesKey = true });
        });
        try
        {
            var delay = Task.Delay(duration, cancellationToken);
            if (await Task.WhenAny(delay, Disconnected) == Disconnected)
                await OnQueue(ThrowIfUnavailable);
            await delay;
        }
        finally
        {
            await OnQueue(() => { _discovered = null; _central?.StopScan(); });
        }
    }

    public async Task ConnectAsync(Guid identifier, CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken);
        await RunOperationAsync("connect", () =>
        {
            if (_peripheral is not null) throw new InvalidOperationException("A peripheral session is already active.");
            using var uuid = new NSUuid(identifier.ToString());
            _peripheral = _central!.RetrievePeripheralsWithIdentifiers([uuid]).FirstOrDefault()
                ?? throw new IOException("Apple could not retrieve the Bluetooth peripheral. Scan for it again.");
            _peripheral.Delegate = _peripheralDelegate;
            _central.ConnectPeripheral(_peripheral);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> DiscoverServicesAsync(CancellationToken cancellationToken)
    {
        await RunOperationAsync("services", () => Peripheral.DiscoverServices(), cancellationToken);
        Guid[] services = [];
        await OnQueue(() => services = (Peripheral.Services ?? []).Select(s => ToGuid(s.UUID)).ToArray());
        return services;
    }

    public async Task<IReadOnlyList<Guid>> DiscoverCharacteristicsAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        await RunOperationAsync("characteristics", () => Peripheral.DiscoverCharacteristics(Service(serviceId)), cancellationToken);
        Guid[] characteristics = [];
        await OnQueue(() => characteristics = (Service(serviceId).Characteristics ?? []).Select(c => ToGuid(c.UUID)).ToArray());
        return characteristics;
    }

    public Task SubscribeAsync(Guid service, Guid characteristic, CancellationToken cancellationToken) =>
        RunOperationAsync("notify", () =>
        {
            var value = Characteristic(service, characteristic);
            if ((value.Properties & (CBCharacteristicProperties.Notify | CBCharacteristicProperties.Indicate)) == 0)
                throw new IOException("The characteristic cannot send notifications.");
            Peripheral.SetNotifyValue(true, value);
        }, cancellationToken);

    public async Task WriteAsync(Guid service, Guid characteristic, byte[] bytes, bool preferWithoutResponse, CancellationToken cancellationToken)
    {
        CBCharacteristicWriteType type = default;
        var maximum = 0;
        await OnQueue(() =>
        {
            ThrowIfUnavailable();
            var properties = Characteristic(service, characteristic).Properties;
            var without = properties.HasFlag(CBCharacteristicProperties.WriteWithoutResponse);
            var with = properties.HasFlag(CBCharacteristicProperties.Write);
            if (!without && !with) throw new IOException("The characteristic is not writable.");
            type = without && (preferWithoutResponse || !with) ? CBCharacteristicWriteType.WithoutResponse : CBCharacteristicWriteType.WithResponse;
            maximum = checked((int)Peripheral.GetMaximumWriteValueLength(type));
        });
        if (maximum <= 0) throw new IOException("Invalid Bluetooth write size.");
        for (var offset = 0; offset < bytes.Length; offset += maximum)
        {
            var chunk = bytes.AsSpan(offset, Math.Min(maximum, bytes.Length - offset)).ToArray();
            if (type == CBCharacteristicWriteType.WithResponse)
                await RunOperationAsync("write", () => Write(chunk), cancellationToken);
            else
            {
                // CoreBluetooth has no write-completion callback for WithoutResponse.
                while (true)
                {
                    Task? ready = null;
                    await OnQueue(() =>
                    {
                        ThrowIfUnavailable();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Peripheral.CanSendWriteWithoutResponse) ready = BeginOperation("write-ready").Task;
                        else Write(chunk);
                    });
                    if (ready is null) break;
                    await AwaitOperationAsync("write-ready", ready, cancellationToken);
                }
            }
            void Write(byte[] data)
            {
                using var value = NSData.FromArray(data);
                Peripheral.WriteValue(value, Characteristic(service, characteristic), type);
            }
        }
    }

    private CBCharacteristic? _readCharacteristic;
    private byte[] _readValue = [];
    public async Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken cancellationToken)
    {
        var readable = false;
        await OnQueue(() => { ThrowIfUnavailable(); readable = Characteristic(service, characteristic).Properties.HasFlag(CBCharacteristicProperties.Read); });
        if (!readable) return null;
        await RunOperationAsync("read", () =>
        {
            _readCharacteristic = Characteristic(service, characteristic);
            Peripheral.ReadValue(_readCharacteristic);
        }, cancellationToken);
        return _readValue;
    }

    private CBPeripheral Peripheral => _peripheral ?? throw new IOException("Bluetooth is not connected.");
    private CBService Service(Guid id) => (Peripheral.Services ?? []).FirstOrDefault(s => ToGuid(s.UUID) == id) ?? throw new IOException($"Missing service {id}.");
    private CBCharacteristic Characteristic(Guid service, Guid id) => (Service(service).Characteristics ?? []).FirstOrDefault(c => ToGuid(c.UUID) == id) ?? throw new IOException($"Missing characteristic {id}.");
    public static Guid ToGuid(CBUUID uuid)
    {
        var text = uuid.ToString();
        return Guid.Parse(text.Length switch { 4 => $"0000{text}-0000-1000-8000-00805f9b34fb", 8 => $"{text}-0000-1000-8000-00805f9b34fb", _ => text });
    }

    private TaskCompletionSource BeginOperation(string key)
    {
        ThrowIfUnavailable();
        var completion = NewCompletion();
        if (!_operations.TryAdd(key, completion)) throw new InvalidOperationException($"Bluetooth operation {key} is already pending.");
        return completion;
    }
    private async Task RunOperationAsync(string key, Action start, CancellationToken cancellationToken)
    {
        Task? operation = null;
        await OnQueue(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = BeginOperation(key);
            operation = completion.Task;
            try { start(); }
            catch { _operations.Remove(key); throw; }
        });
        await AwaitOperationAsync(key, operation!, cancellationToken);
    }
    private async Task AwaitOperationAsync(string key, Task operation, CancellationToken cancellationToken)
    {
        try { await operation.WaitAsync(OperationTimeout, cancellationToken); }
        catch (Exception ex)
        {
            // Never allow an old callback to complete a later request after a timeout.
            await OnQueue(() => Fail(ex));
            throw;
        }
        finally { await OnQueue(() => _operations.Remove(key)); }
    }
    private void Complete(string key, NSError? error)
    {
        if (_closing || _failure is not null || !_operations.TryGetValue(key, out var completion)) return;
        if (error is null) completion.TrySetResult();
        else Fail(new IOException($"Bluetooth {key} failed: {error.LocalizedDescription}"));
    }
    private void Fail(Exception error)
    {
        if (_failure is not null || _closing) return;
        _failure = error;
        foreach (var operation in _operations.Values) operation.TrySetException(error);
        _disconnected.TrySetResult();
        ConnectionFailed?.Invoke(error);
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var waitForDisconnect = false;
        await OnQueue(() =>
        {
            Fail(new IOException("Bluetooth connection is closing."));
            _stateChanged?.TrySetException(new ObjectDisposedException(nameof(AppleBleTransport)));
            _closing = true;
            _discovered = null;
            _central?.StopScan();
            if (_peripheral is not null && _peripheral.State != CBPeripheralState.Disconnected)
            {
                waitForDisconnect = true;
                _central?.CancelPeripheralConnection(_peripheral);
            }
        });
        if (waitForDisconnect)
        {
            try { await _disconnectConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
        await OnQueue(() =>
        {
            if (_peripheral is not null) _peripheral.Delegate = null!;
            _peripheral = null;
            if (_central is not null) { _central.Delegate = null!; _central.Dispose(); _central = null; }
            _peripheralDelegate.Dispose();
            _centralDelegate.Dispose();
        });
    }
    private readonly TaskCompletionSource _disconnectConfirmed = NewCompletion();

    private sealed class CentralDelegate(AppleBleTransport owner) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central)
        {
            owner._stateChanged?.TrySetResult();
            owner._stateChanged = null;
            if (!owner._closing && central.State != CBManagerState.PoweredOn && (owner._peripheral is not null || owner._discovered is not null))
                owner.Fail(central.State == CBManagerState.Unauthorized ? new UnauthorizedAccessException("Bluetooth permission denied.") : new IOException($"Bluetooth state: {central.State}"));
        }
        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral, NSDictionary advertisementData, NSNumber RSSI)
        {
            if (owner._closing || owner._failure is not null || owner._discovered is null) return;
            var name = (advertisementData[CBAdvertisement.DataLocalNameKey] as NSString)?.ToString() ?? peripheral.Name ?? "";
            var services = advertisementData[CBAdvertisement.DataServiceUUIDsKey] as NSArray;
            var ids = services is null ? [] : Enumerable.Range(0, (int)services.Count).Select(i => ToGuid(services.GetItem<CBUUID>((nuint)i))).ToArray();
            owner._discovered(new(Guid.Parse(peripheral.Identifier.AsString()), name, RSSI.Int16Value, ids));
        }
        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral) => owner.Complete("connect", null);
        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) => owner.Fail(new IOException($"Bluetooth connection failed: {error?.LocalizedDescription ?? "unknown error"}"));
        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
        {
            owner._disconnectConfirmed.TrySetResult();
            owner.Fail(new IOException($"Bluetooth disconnected: {error?.LocalizedDescription ?? "connection ended"}"));
        }
    }
    private sealed class PeripheralDelegate(AppleBleTransport owner) : CBPeripheralDelegate
    {
        public override void DiscoveredService(CBPeripheral peripheral, NSError? error) => owner.Complete("services", error);
        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error) => owner.Complete("characteristics", error);
        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error is null && !characteristic.IsNotifying) owner.Fail(new IOException("Bluetooth notifications were not enabled."));
            else owner.Complete("notify", error);
        }
        public override void WroteCharacteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error) => owner.Complete("write", error);
        public override void IsReadyToSendWriteWithoutResponse(CBPeripheral peripheral) => owner.Complete("write-ready", null);
        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (owner._closing || owner._failure is not null) return;
            if (error is not null) { owner.Fail(new IOException($"Bluetooth value failed: {error.LocalizedDescription}")); return; }
            var bytes = characteristic.Value?.ToArray() ?? [];
            if (owner._readCharacteristic?.Handle == characteristic.Handle)
            {
                owner._readValue = bytes;
                owner._readCharacteristic = null;
                owner.Complete("read", null);
            }
            else owner.Notification?.Invoke(ToGuid(characteristic.UUID), bytes);
        }
        public override void ModifiedServices(CBPeripheral peripheral, CBService[] services) => owner.Fail(new IOException("Bluetooth services changed; reconnect required."));
    }
}
#endif
