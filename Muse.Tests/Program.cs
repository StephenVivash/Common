using System.Text;
using Common.Bluetooth;
using Muse.Core;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
void Plan(params SessionPlan[] plans)
{
    Check(AppleBleTransport.Plans.Count == 0, "Unconsumed transport plans.");
    foreach (var plan in plans) AppleBleTransport.Plans.Enqueue(plan);
}
static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(4));
static async Task Stop(Task stream, CancellationTokenSource cts)
{
    cts.Cancel();
    try { await stream.WaitAsync(TimeSpan.FromSeconds(4)); }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
}
static string Command(byte[] bytes) => Encoding.ASCII.GetString(bytes, 1, bytes.Length - 2);
static byte[] EegPacket(int sequence)
{
    var bytes = new byte[20];
    bytes[0] = (byte)(sequence >> 8);
    bytes[1] = (byte)sequence;
    for (var i = 0; i < 12; i += 2)
    {
        var x = (int)(2048 + 100 * Math.Sin(2 * Math.PI * 10 * (sequence * 12 + i) / 256));
        var y = (int)(2048 + 100 * Math.Sin(2 * Math.PI * 10 * (sequence * 12 + i + 1) / 256));
        var offset = 2 + i / 2 * 3;
        bytes[offset] = (byte)(x >> 4);
        bytes[offset + 1] = (byte)((x << 4) | (y >> 8));
        bytes[offset + 2] = (byte)y;
    }
    return bytes;
}
var identifier = Guid.Parse("b1524c23-a076-46e2-9f85-737ad29e4539");
var headset = MuseDeviceAdvertisement.FromPeripheral(identifier, "Muse-Test", -50, true);
Check(headset.PeripheralIdentifier == identifier && headset.BluetoothAddress == 0 && headset.DeviceIdentifier == identifier.ToString(), "Apple UUID must remain intact.");
Check(new MuseDeviceAdvertisement(0x123456789ABC, "", -60, false).DeviceIdentifier == "0x123456789ABC", "Windows/Android address changed.");

// Name-only and service-only discovery, unrelated devices, timeouts and terminal errors.
foreach (var byService in new[] { false, true })
{
    var plan = new SessionPlan { Advertisements = [
        new(Guid.NewGuid(), "Unrelated", -40, []),
        new(identifier, byService ? "" : "mUsE-Test", -50, byService ? [MuseBluetoothConstants.MuseServiceUuid] : [])] };
    Plan(plan);
    await using var client = new MuseBtClient();
    var found = await client.FindMuseAsync(TimeSpan.FromSeconds(1));
    Check(found?.PeripheralIdentifier == identifier && found.ServiceAdvertised == byService && plan.Disposed, "Muse discovery/disposal failed.");
}
{
    var plan = new SessionPlan();
    Plan(plan);
    await using var client = new MuseBtClient();
    Check(await client.FindMuseAsync(TimeSpan.FromMilliseconds(10)) is null && plan.Disposed, "Scan timeout failed.");
}
foreach (var error in new Exception[] { new UnauthorizedAccessException("Denied"), new IOException("Powered off") })
{
    var plan = new SessionPlan { ScanError = error };
    Plan(plan);
    await using var client = new MuseBtClient();
    if (error is UnauthorizedAccessException)
        await Throws<UnauthorizedAccessException>(() => client.FindMuseAsync(TimeSpan.FromSeconds(1)));
    else
        await Throws<IOException>(() => client.FindMuseAsync(TimeSpan.FromSeconds(1)));
    Check(plan.Disposed, "Failed scan leaked its transport.");
}
{
    var plan = new SessionPlan();
    Plan(plan);
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    await using var client = new MuseBtClient();
    await Throws<OperationCanceledException>(() => client.FindMuseAsync(TimeSpan.FromSeconds(10), cts.Token));
    Check(plan.Disposed, "Canceled scan leaked its transport.");
}

// Run the actual Apple adapter and common stream code, including FFT publication.
{
    var plan = new SessionPlan();
    Plan(plan);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var notifications = new List<MuseNotification>();
    var bands = new TaskCompletionSource<MuseBandPowerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.NotificationReceived += (_, value) => notifications.Add(value);
    client.BandPowersCalculated += (_, value) => bands.TrySetResult(value);
    var stream = client.ConnectAndStreamAsync(headset, cts.Token);
    await Wait(plan.Started.Task);
    Check(plan.ConnectedIdentifier == identifier, "Connect used the wrong UUID.");
    Check(plan.Subscriptions.SequenceEqual(new[] { MuseBluetoothConstants.ControlUuid }.Concat(MuseBluetoothConstants.Sensors.Select(s => s.Uuid))), "Sensor subscription order changed.");
    Check(plan.Writes.Select(Convert.ToHexString).SequenceEqual(new[] { "02680A", "02730A", "047032310A", "02640A" }), "Muse start sequence/framing changed.");
    for (var i = 0; i < 24; i++) plan.Transport.Emit(MuseBluetoothConstants.MuseUuid(3), EegPacket(i));
    plan.Transport.Emit(MuseBluetoothConstants.MuseUuid(11), new byte[10]);
    plan.Transport.Emit(MuseBluetoothConstants.MuseUuid(9), new byte[20]);
    plan.Transport.Emit(MuseBluetoothConstants.MuseUuid(15), new byte[20]);
    var reading = await bands.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(reading.SensorName == "EEG TP9" && double.IsFinite(reading.Bands.AlphaAbsolute), "EEG band publication failed.");
    Check(notifications[23].Count == 24 && notifications[^3].Kind == MuseSensorKind.Telemetry && notifications[^2].Kind == MuseSensorKind.Imu && notifications[^1].Kind == MuseSensorKind.Raw, "Notification routing/counting failed.");
    await Throws<InvalidOperationException>(() => client.SendControlCommandAsync(new string('x', 255)));
    await client.SendControlCommandAsync(new string('x', 254));
    Check(plan.Writes[^1][0] == 255 && plan.Writes[^1].Length == 256, "Maximum command framing failed.");
    await Stop(stream, cts);
    Check(plan.Disposed && Command(plan.Writes[^1]) == "h", "Stop failed to halt and dispose.");
}

// Disconnection creates a fresh transport using the same UUID; partial FFT
// windows and packet counts must not survive reconnection. Optional sensors may be absent.
{
    var chars = new[] { MuseBluetoothConstants.ControlUuid, MuseBluetoothConstants.MuseUuid(3) };
    var first = new SessionPlan { Characteristics = chars };
    var second = new SessionPlan { Characteristics = chars };
    Plan(first, second);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var counts = new List<long>();
    var publications = 0;
    client.NotificationReceived += (_, n) => counts.Add(n.Count);
    client.BandPowersCalculated += (_, _) => publications++;
    var stream = client.ConnectAndStreamAsync(headset, cts.Token);
    await Wait(first.Started.Task);
    for (var i = 0; i < 12; i++) first.Transport.Emit(chars[1], EegPacket(i));
    first.Transport.Fail();
    await Wait(second.Started.Task);
    for (var i = 0; i < 12; i++) second.Transport.Emit(chars[1], EegPacket(i));
    await Task.Delay(200);
    Check(first.Disposed && second.ConnectedIdentifier == identifier && counts[12] == 1, "Reconnect/count reset failed.");
    Check(publications == 0, "FFT window crossed disconnected sessions.");
    var before = counts.Count;
    first.Transport.Emit(chars[1], EegPacket(100));
    Check(counts.Count == before, "Old transport notification entered the new stream.");
    await Stop(stream, cts);
    Check(second.Disposed, "Replacement transport leaked.");
}

// A silent headset is restarted after the notification timeout, with keepalive
// and halt commands sent before the old transport is disposed.
{
    var first = new SessionPlan();
    var second = new SessionPlan();
    Plan(first, second);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var stream = client.ConnectAndStreamAsync(headset, cts.Token);
    await Wait(first.Started.Task);
    await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(13));
    Check(first.Disposed && first.Writes.Any(w => Command(w) == "k") && Command(first.Writes[^1]) == "h", "Stall recovery/keepalive failed.");
    await Stop(stream, cts);
    Check(second.Disposed, "Stall recovery session leaked.");
}

// Missing required characteristics and failed command writes clean up before retry.
foreach (var plan in new[] {
    new SessionPlan { Services = [] },
    new SessionPlan { Characteristics = [] },
    new SessionPlan { Characteristics = [MuseBluetoothConstants.ControlUuid] },
    new SessionPlan { WriteError = new TimeoutException("Write timed out") } })
{
    Plan(plan);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var retried = false;
    client.ConnectionStatusChanged += (_, status) => { if (status == "Reconnecting") { retried = true; cts.Cancel(); } };
    await Stop(client.ConnectAndStreamAsync(headset, cts.Token), cts);
    Check(plan.Disposed && retried, "Failed setup did not clean up and request reconnect.");
}
{
    var plan = new SessionPlan { HoldWrite = true };
    Plan(plan);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var stream = client.ConnectAndStreamAsync(headset, cts.Token);
    await Wait(plan.Writing.Task);
    await Stop(stream, cts);
    Check(plan.Disposed, "Canceling a pending native operation leaked its session.");
}
// Denied connection is terminal, rather than an endless reconnect loop.
{
    var plan = new SessionPlan { ConnectError = new UnauthorizedAccessException("Denied") };
    Plan(plan);
    await using var client = new MuseBtClient();
    await Throws<UnauthorizedAccessException>(() => client.ConnectAndStreamAsync(headset));
    Check(plan.Disposed, "Denied connection leaked its transport.");
}
// A halt that never acknowledges is bounded, so Stop still releases the radio.
{
    var plan = new SessionPlan();
    Plan(plan);
    await using var client = new MuseBtClient();
    using var cts = new CancellationTokenSource();
    var stream = client.ConnectAndStreamAsync(headset, cts.Token);
    await Wait(plan.Started.Task);
    plan.HoldWrite = true;
    await Stop(stream, cts);
    Check(plan.Disposed, "Unacknowledged halt prevented cleanup.");
}
{
    await using var client = new MuseBtClient();
    await Throws<ArgumentException>(() => client.ConnectAndStreamAsync(new(123, "Muse", -50, true)));
}
Check(AppleBleTransport.Plans.Count == 0, "Not all sessions exercised.");
Console.WriteLine($"PASS: {checks} Muse Apple adapter/shared stream checks (simulated transport; no Bluetooth radio used).");
