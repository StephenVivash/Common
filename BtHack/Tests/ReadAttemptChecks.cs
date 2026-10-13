using WattCycle.Core;

internal static class ReadAttemptChecks
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Timeout while Windows owns a native request must not release its handles
        // or start another request until that request actually finishes.
        using var deadline = new CancellationTokenSource();
        var native = Signal<int>();
        var cleanupStarted = Signal<bool>();
        var cleanupFinished = Signal<bool>();
        var readCalls = 0;
        var attempt = BluetoothReadAttempt.RunAsync(
            async () => { await BluetoothReadAttempt.AwaitNativeAsync(() => native.Task, deadline.Token); },
            () => { readCalls++; return Task.FromResult(42); },
            async () => { cleanupStarted.SetResult(true); await cleanupFinished.Task; }, deadline.Token);
        deadline.Cancel();
        Check(!attempt.IsCompleted && !cleanupStarted.Task.IsCompleted, "Cancellation released an active native operation.");
        native.SetResult(1);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(readCalls == 0, "A read was queued after the deadline.");
        Check(!attempt.IsCompleted, "Timed-out attempt returned before cleanup completed.");
        cleanupFinished.SetResult(true);
        try { await attempt; throw new Exception("Cancelled attempt returned success."); }
        catch (OperationCanceledException) { checks++; }

        var started = false;
        try
        {
            await BluetoothReadAttempt.AwaitNativeAsync(() => { started = true; return Task.FromResult(1); }, deadline.Token);
            throw new Exception("Expired deadline accepted a native request.");
        }
        catch (OperationCanceledException) { Check(!started, "Expired deadline queued another native request."); }

        // A successful reading also belongs to its connection until cleanup ends.
        var release = Signal<bool>();
        var closing = Signal<bool>();
        var success = BluetoothReadAttempt.RunAsync(() => Task.CompletedTask, () => Task.FromResult(99),
            async () => { closing.SetResult(true); await release.Task; }, CancellationToken.None);
        await closing.Task;
        Check(!success.IsCompleted, "Successful reading returned while its connection was still open.");
        release.SetResult(true);
        Check(await success == 99, "Cleanup changed the successful reading.");

        // Alternate failed connects, failed reads, and healthy sessions. Failures
        // must be exposed immediately, with no retries or retained active session.
        var active = 0;
        var connects = 0;
        var cleanups = 0;
        var reads = 0;
        for (var i = 0; i < 90; i++)
        {
            var mode = i % 3;
            var id = i;
            try
            {
                var result = await BluetoothReadAttempt.RunAsync(
                    () =>
                    {
                        Check(active == 0, "Previous attempt retained a connection.");
                        active++; connects++;
                        return mode == 0 ? Task.FromException(new IOException("Connect failed")) : Task.CompletedTask;
                    },
                    () =>
                    {
                        reads++;
                        return mode == 1 ? Task.FromException<int>(new TimeoutException("Read failed")) : Task.FromResult(id);
                    },
                    () => { active--; cleanups++; return Task.CompletedTask; }, CancellationToken.None);
                Check(mode == 2 && result == id, "Failed attempt returned success, or fresh reading was lost.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Check(mode != 2, "A healthy session inherited a prior failure.");
            }
            Check(active == 0 && connects == i + 1 && cleanups == i + 1, "Attempt retried or failed to release its connection.");
        }
        Check(reads == 60, "Reads ran after failed connection attempts.");
        return checks;
    }
}
