using Muse.Core;

namespace Common.Bluetooth;

// Test double at the native boundary. The real Apple adapter and shared Muse
// client are compiled into this executable; no radio, headset or Apple SDK is used.
public sealed record AppleBleAdvertisement(Guid Identifier, string Name, short Rssi, IReadOnlyList<Guid> Services);

internal sealed class SessionPlan
{
    public AppleBleAdvertisement[] Advertisements = [];
    public Guid[] Services = [MuseBluetoothConstants.MuseServiceUuid];
    public Guid[] Characteristics = [MuseBluetoothConstants.ControlUuid, .. MuseBluetoothConstants.Sensors.Select(s => s.Uuid)];
    public Exception? ScanError;
    public Exception? ConnectError;
    public Exception? WriteError;
    public bool HoldWrite;
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AppleBleTransport Transport = null!;
    public Guid? ConnectedIdentifier;
    public readonly List<Guid> Subscriptions = [];
    public readonly List<byte[]> Writes = [];
    public bool Disposed;
}

public sealed class AppleBleTransport : IAsyncDisposable
{
    internal static readonly Queue<SessionPlan> Plans = new();
    private readonly SessionPlan _plan;
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Disconnected => _disconnected.Task;
    public event Action<Guid, byte[]>? Notification;
    public event Action<Exception>? ConnectionFailed;

    public AppleBleTransport()
    {
        if (!Plans.TryDequeue(out var plan)) throw new Exception("Unexpected Bluetooth session.");
        _plan = plan;
        plan.Transport = this;
    }
    public async Task ScanAsync(TimeSpan duration, Action<AppleBleAdvertisement> discovered, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_plan.ScanError is { } error) throw error;
        foreach (var advertisement in _plan.Advertisements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered(advertisement);
        }
        await Task.Delay(duration, cancellationToken);
    }
    public Task ConnectAsync(Guid identifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_plan.ConnectError is { } error) throw error;
        _plan.ConnectedIdentifier = identifier;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<Guid>> DiscoverServicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(_plan.Services);
    public Task<IReadOnlyList<Guid>> DiscoverCharacteristicsAsync(Guid service, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(_plan.Characteristics);
    public Task SubscribeAsync(Guid service, Guid characteristic, CancellationToken cancellationToken)
    {
        _plan.Subscriptions.Add(characteristic);
        return Task.CompletedTask;
    }
    public async Task WriteAsync(Guid service, Guid characteristic, byte[] bytes, bool preferWithoutResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (service != MuseBluetoothConstants.MuseServiceUuid || characteristic != MuseBluetoothConstants.ControlUuid || preferWithoutResponse)
            throw new Exception("Wrong control write route or preference.");
        _plan.Writing.TrySetResult();
        if (_plan.HoldWrite) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (_plan.WriteError is { } error) throw error;
        _plan.Writes.Add(bytes);
        if (bytes.SequenceEqual(new byte[] { 2, (byte)'d', 10 })) _plan.Started.TrySetResult();
    }
    internal void Emit(Guid uuid, byte[] bytes) => Notification?.Invoke(uuid, bytes);
    internal void Fail()
    {
        _disconnected.TrySetResult();
        ConnectionFailed?.Invoke(new IOException("Simulated disconnect."));
    }
    public ValueTask DisposeAsync()
    {
        _plan.Disposed = true;
        _disconnected.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
