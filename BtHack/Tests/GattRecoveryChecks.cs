using WattCycle.Core;

internal static class GattRecoveryChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }

        async Task ExpectFailure(Task task, Exception error)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1));
                throw new Exception("A failed Bluetooth session reported success.");
            }
            catch (IOException ex)
            {
                Check(ReferenceEquals(ex, error), "Bluetooth operation lost the original connection failure.");
            }
        }

        // A connection error must wake discovery as well as the initial connect waiter.
        var failedConnect = new GattSessionOperations<int, byte[]>();
        var connectError = new IOException("GATT 133 during connection");
        failedConnect.Fail(connectError);
        await ExpectFailure(failedConnect.Connected, connectError);
        await ExpectFailure(failedConnect.Services, connectError);
        Check(!failedConnect.IsActive && !failedConnect.Disconnected.IsCompleted,
            "A GATT error should stop operations without inventing a disconnect confirmation.");
        failedConnect.ConfirmDisconnected();
        Check(failedConnect.Disconnected.IsCompletedSuccessfully, "Actual disconnect was lost after failure.");

        // A drop after connection previously left service discovery waiting until the page timeout.
        var failedDiscovery = new GattSessionOperations<int, byte[]>();
        failedDiscovery.CompleteConnected();
        var discoveryError = new IOException("Disconnected during discovery");
        failedDiscovery.Fail(discoveryError);
        failedDiscovery.CompleteServices(0); // Delayed callback from the dead connection.
        await ExpectFailure(failedDiscovery.Services, discoveryError);
        Check(failedDiscovery.Connected.IsCompletedSuccessfully, "Failure changed an already completed connect result.");

        // Repeat failed reads and new sessions to catch errors retained across reconnects.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var failed = new GattSessionOperations<int, byte[]>();
            failed.CompleteConnected();
            failed.CompleteServices(0);
            var descriptor = failed.BeginDescriptorWrite();
            var write = failed.BeginCharacteristicWrite();
            var read = failed.BeginCharacteristicRead();
            var error = new IOException($"Connection dropped on attempt {attempt}");
            failed.Fail(error);
            failed.Fail(new IOException("Later cleanup error"));
            failed.CompleteDescriptorWrite(0);
            failed.CompleteCharacteristicWrite(0);
            failed.CompleteCharacteristicRead([99]);
            await ExpectFailure(descriptor, error);
            await ExpectFailure(write, error);
            await ExpectFailure(read, error);
            try
            {
                _ = failed.BeginCharacteristicRead();
                throw new Exception("Failed connection accepted a new operation.");
            }
            catch (IOException ex)
            {
                Check(ReferenceEquals(ex.InnerException, error), "New operation did not retain the failure cause.");
            }

            var fresh = new GattSessionOperations<int, byte[]>();
            fresh.CompleteConnected();
            fresh.CompleteServices(0);
            var newWrite = fresh.BeginCharacteristicWrite();
            var newRead = fresh.BeginCharacteristicRead();
            failed.CompleteCharacteristicRead([99]);
            Check(!newRead.IsCompleted, "Old callback completed a new connection's read.");
            fresh.CompleteCharacteristicWrite(0);
            fresh.CompleteCharacteristicRead([1, 2]);
            Check(await newWrite == 0 && (await newRead).SequenceEqual(new byte[] { 1, 2 }) && fresh.IsActive,
                "A failed connection poisoned the next session.");
            fresh.BeginClose();
            fresh.ConfirmDisconnected();
        }

        var closing = new GattSessionOperations<int, byte[]>();
        closing.CompleteConnected();
        closing.CompleteServices(0);
        var closingRead = closing.BeginCharacteristicRead();
        closing.BeginClose();
        closing.CompleteCharacteristicRead([99]);
        Check(closingRead.IsFaulted && !closing.IsActive, "Late callback revived a closing session.");
        _ = closingRead.Exception;
        Check(!closing.Disconnected.IsCompleted, "Cleanup skipped waiting for the real disconnect.");
        closing.ConfirmDisconnected();
        await closing.Disconnected.WaitAsync(TimeSpan.FromSeconds(1));
        Check(closing.Disconnected.IsCompletedSuccessfully, "Disconnect confirmation was ignored during cleanup.");

        return checks;
    }
}
