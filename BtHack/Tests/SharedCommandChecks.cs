using System.Buffers.Binary;

namespace WattCycle.Core;

// Compile the actual shared client with an in-memory transport.
public sealed partial class WattCycleBtClient
{
#pragma warning disable CS0067
    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<string>? InfoMessage;
    public event EventHandler<string>? ConnectionStatusChanged;
    public event EventHandler<WattCycleBatteryReading>? BatteryReadingReceived;
#pragma warning restore CS0067
    private Func<byte[], Task> _send = _ => Task.CompletedTask;
    private int _cleanupCalls;
    private Task ConnectForCommandAsync(WattCycleDeviceAdvertisement advertisement, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private Task CleanupConnectionAsync() { _cleanupCalls++; return Task.CompletedTask; }
    private Task ConnectAndPollOnceAsync(WattCycleDeviceAdvertisement advertisement, CancellationToken token) => Task.CompletedTask;
    private Task WriteCommandAsync(byte[] bytes, bool preferWithoutResponse, CancellationToken token) { token.ThrowIfCancellationRequested(); return _send(bytes); }
    private Task<JbdFrame?> TryReadNotifyCharacteristicAsync(byte register, CancellationToken token) => Task.FromResult<JbdFrame?>(null);

    public static async Task<int> RunProtocolChecksAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        async Task Throws<T>(Func<Task> action) where T : Exception
        {
            try { await action(); throw new Exception($"Expected {typeof(T).Name}"); }
            catch (T) { checks++; }
        }
        var advertisement = new WattCycleDeviceAdvertisement(1, "WT test", -40, true);
        var tdt = new WattCycleBtClient { _protocolKind = BmsProtocolKind.Tdt };
        var writes = new List<byte[]>();
        var status = Convert.FromHexString("7E000103008C0020040CF10CEF0CF20CEC040B6A0B860B790B75C024052C00CD0130008903E80043BEE30D");
        var protection = Convert.FromHexString("7E000103008D0018040000000004000000000000000200000E09000018000000C1120D");
        tdt._send = bytes =>
        {
            writes.Add(bytes);
            var normalized = (byte[])bytes.Clone(); normalized[0] = 0x7e; UpdateCrc(normalized);
            Check(TdtProtocol.TryParseFrame(normalized, out var command), "Shared path sent an invalid TDT command");
            if (command.Function == 0x03)
            {
                // A notification can arrive before the local write completes.
                var reply = command.Command == 0x8c ? status : protection;
                for (var offset = 0; offset < reply.Length; offset += 3)
                    tdt.HandleNotification(reply.AsSpan(offset, Math.Min(3, reply.Length - offset)).ToArray());
            }
            else
            {
                var bit = command.Command == 0x82 ? 2 : 4;
                protection[8 + 16] = (byte)(bytes[8] != 0 ? protection[24] | bit : protection[24] & ~bit);
                UpdateCrc(protection);
                var ack = Convert.FromHexString(command.Command == 0x82 ? "7E000106008200005AEF0D" : "7E000106008300009ABE0D");
                UpdateCrc(ack);
                tdt.HandleNotification(ack);
            }
            return Task.CompletedTask;
        };
        var reading = await tdt.ReadBatteryAsync(default);
        Check(reading.PackVoltage == 13.24 && reading.Current == -3.6, "Shared TDT decoding changed");
        var result = await tdt.SetMosAsync(advertisement, false, false);
        Check(result.Matches && !result.Reading.ChargeMosEnabled && !result.Reading.DischargeMosEnabled, "Shared MOS readback failed");
        Check(writes.Count(b => b[3] == 6) == 2, "MOS writes were retried or skipped");
        Check(tdt._cleanupCalls == 1, "MOS connection was not released");
        Check(tdt._pendingTdtCommand is null, "Completed TDT command leaked");

        // Header fallback is confined to reads, and a successful header is reused.
        var respond = tdt._send;
        writes.Clear();
        tdt._send = bytes =>
        {
            if (bytes[0] == 0x1e) { writes.Add(bytes); return Task.CompletedTask; }
            return respond(bytes);
        };
        await tdt.ReadBatteryAsync(default);
        Check(writes.Count == 3 && writes[0][0] == 0x1e && writes.Skip(1).All(b => b[0] == 0x7e), "Working TDT header was not learned and reused");

        // Commands that apply without an ACK are verified, never sent again.
        writes.Clear();
        tdt._send = bytes =>
        {
            if (bytes[3] == 6)
            {
                writes.Add(bytes);
                var bit = bytes[5] == 0x82 ? 2 : 4;
                protection[24] = (byte)(bytes[8] != 0 ? protection[24] | bit : protection[24] & ~bit);
                UpdateCrc(protection);
                return Task.CompletedTask;
            }
            return respond(bytes);
        };
        Check((await tdt.SetMosAsync(advertisement, true, true)).Matches, "Applied commands without ACK were not verified by readback");
        Check(writes.Count(b => b[3] == 6) == 2, "Missing ACK caused duplicate MOS writes");

        // No response-header fallback after a native transport write timeout.
        writes.Clear();
        tdt._send = bytes => { writes.Add(bytes); throw new TimeoutException("Native write timed out"); };
        await Throws<TimeoutException>(() => tdt.SendTdtReadCommandAsync(0x8c, default));
        Check(writes.Count == 1 && tdt._pendingTdtCommand is null, "Native timeout retried a command or leaked pending state");
        await Throws<TimeoutException>(() => tdt.SetMosAsync(advertisement, true, true));
        Check(tdt._cleanupCalls == 3, "Failed MOS connection was not released");

        // Error ACKs must stop the sequence, even though an earlier MOS write may apply.
        tdt._send = bytes =>
        {
            tdt.HandleNotification(Convert.FromHexString("7E00018600820001011CC40D"));
            return Task.CompletedTask;
        };
        await Throws<InvalidOperationException>(() => tdt.SendTdtMosSwitchAsync(true, true, default));
        Check(tdt._pendingTdtCommand is null, "Rejected MOS command leaked");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => tdt.SendTdtReadCommandAsync(0x8c, canceled.Token));
        Check(tdt._pendingTdtCommand is null, "Canceled TDT command leaked");

        var jbd = new WattCycleBtClient();
        jbd._send = bytes =>
        {
            var response = JbdReply(bytes[2], 0, bytes[2] == 0x04 ? [0x0c, 0xe4, 0x0c, 0xe5] : []);
            jbd.HandleNotification(response[..3]);
            jbd.HandleNotification(response[3..]);
            return Task.CompletedTask;
        };
        var cells = await jbd.SendReadCommandAsync(4, default);
        Check(JbdProtocol.TryParseCellVoltages(cells, out var voltages) && voltages.SequenceEqual(new[] { 3.3, 3.301 }), "Shared JBD fragmented response failed");
        Check((await jbd.SendJbdWriteCommandAsync(0xe1, [0, 3], default)).Status == 0, "Shared JBD ACK failed");
        jbd._send = bytes => { jbd.HandleNotification(JbdReply(0xe1, 0x80, [])); return Task.CompletedTask; };
        await Throws<InvalidOperationException>(() => jbd.SendJbdWriteCommandAsync(0xe1, [0, 3], default));
        Check(jbd._pendingCommand is null, "Rejected JBD command leaked");
        jbd._send = bytes => throw new IOException("Disconnected");
        await Throws<IOException>(() => jbd.SendReadCommandAsync(3, default));
        Check(jbd._pendingCommand is null, "Disconnected JBD command leaked");

        var id = Guid.Parse("D57F369A-82E7-4D74-8CCF-F85C54473B36");
        var first = WattCycleDeviceAdvertisement.FromPeripheral(id, "WT", -70, true);
        var later = WattCycleDeviceAdvertisement.FromPeripheral(id, "WT renamed", -40, false);
        Check(first.BluetoothAddress == later.BluetoothAddress && first.BluetoothAddress != 0, "Apple history key was unstable");
        Check(first.PeripheralIdentifier == id, "Apple identifier was lost");
        Check((first with { Name = "renamed" }).PeripheralIdentifier == id, "Cloning lost Apple identifier");
        Check(first.BluetoothAddress != WattCycleDeviceAdvertisement.FromPeripheral(Guid.Parse("D57F369A-82E7-4D74-8CCF-F85C54473B37"), "WT", 0, false).BluetoothAddress, "Distinct peripherals have the same history key");
        return checks;
    }
    private static byte[] JbdReply(byte register, byte status, byte[] payload)
    {
        var frame = new byte[payload.Length + 7];
        frame[0] = 0xdd; frame[1] = register; frame[2] = status; frame[3] = (byte)payload.Length;
        payload.CopyTo(frame, 4);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 3), unchecked((ushort)-frame.AsSpan(1, payload.Length + 3).ToArray().Sum(b => b)));
        frame[^1] = 0x77;
        return frame;
    }
    private static void UpdateCrc(byte[] frame)
    {
        var crc = 0xffff;
        foreach (var value in frame[..^3])
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xa001;
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 3), (ushort)crc);
    }
}
