namespace WattCycle.Core;

// One instance belongs to one native GATT connection. A failed session cannot be reused.
internal sealed class GattSessionOperations<TStatus, TRead>
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TStatus> _services = NewCompletion<TStatus>();
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<TStatus>? _descriptorWrite;
    private TaskCompletionSource<TStatus>? _characteristicWrite;
    private TaskCompletionSource<TRead>? _characteristicRead;
    private Exception? _failure;

    public Task Connected => _connected.Task;
    public Task<TStatus> Services => _services.Task;
    public Task Disconnected => _disconnected.Task;
    public bool IsActive { get { lock (_gate) return _failure is null; } }

    public Task<TStatus> BeginDescriptorWrite()
    {
        lock (_gate)
        {
            ThrowIfFailed();
            return BeginOperation(ref _descriptorWrite);
        }
    }

    public Task<TStatus> BeginCharacteristicWrite()
    {
        lock (_gate)
        {
            ThrowIfFailed();
            return BeginOperation(ref _characteristicWrite);
        }
    }

    public Task<TRead> BeginCharacteristicRead()
    {
        lock (_gate)
        {
            ThrowIfFailed();
            return BeginOperation(ref _characteristicRead);
        }
    }

    public void CompleteConnected()
    {
        lock (_gate) { if (_failure is null) _connected.TrySetResult(); }
    }

    public void CompleteServices(TStatus status)
    {
        lock (_gate) { if (_failure is null) _services.TrySetResult(status); }
    }

    public void CompleteDescriptorWrite(TStatus status)
    {
        lock (_gate) { if (_failure is null) _descriptorWrite?.TrySetResult(status); }
    }

    public void CompleteCharacteristicWrite(TStatus status)
    {
        lock (_gate) { if (_failure is null) _characteristicWrite?.TrySetResult(status); }
    }

    public void CompleteCharacteristicRead(TRead result)
    {
        lock (_gate) { if (_failure is null) _characteristicRead?.TrySetResult(result); }
    }

    public void ConfirmDisconnected() => _disconnected.TrySetResult();

    public void Fail(Exception error)
    {
        lock (_gate)
        {
            _failure ??= error;
            _connected.TrySetException(_failure);
            _services.TrySetException(_failure);
            _descriptorWrite?.TrySetException(_failure);
            _characteristicWrite?.TrySetException(_failure);
            _characteristicRead?.TrySetException(_failure);
        }
    }

    public void BeginClose() => Fail(new IOException("Android Bluetooth connection is closing."));

    private void ThrowIfFailed()
    {
        if (_failure is not null) throw new IOException("Android Bluetooth connection is no longer usable.", _failure);
    }

    private static Task<T> BeginOperation<T>(ref TaskCompletionSource<T>? completion)
    {
        if (completion is { Task.IsCompleted: false })
            throw new InvalidOperationException("A Bluetooth operation of this type is already pending.");
        completion = NewCompletion<T>();
        return completion.Task;
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
