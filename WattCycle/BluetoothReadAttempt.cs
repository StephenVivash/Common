namespace WattCycle.Core;

// A scheduled poll owns one connection, including cleanup on failure/cancellation.
internal static class BluetoothReadAttempt
{
    internal static async Task<T> RunAsync<T>(Func<Task> connect, Func<Task<T>> read,
        Func<Task> cleanup, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connect();
            cancellationToken.ThrowIfCancellationRequested();
            var result = await read();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            await cleanup();
        }
    }

    // Windows cannot cancel an in-flight GATT request. Drain it before releasing
    // its service/device, then honour cancellation before starting another request.
    internal static async Task<T> AwaitNativeAsync<T>(Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await operation();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
