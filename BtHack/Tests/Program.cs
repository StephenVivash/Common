using WattCycle.Core;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

// Fixed vectors from the APK's wire layout, independently calculated CRC16/MODBUS.
var vectors = new (byte Head, bool Charge, bool Enabled, string Hex)[]
{
    (0x1e, true, false, "1E00010600820001001EB20D"),
    (0x1e, true, true, "1E0001060082000101DE730D"),
    (0x1e, false, false, "1E0001060083000100E2B30D"),
    (0x1e, false, true, "1E000106008300010122720D"),
    (0x7e, true, false, "7E00010600820001001C1A0D"),
    (0x7e, true, true, "7E0001060082000101DCDB0D"),
    (0x7e, false, false, "7E0001060083000100E01B0D"),
    (0x7e, false, true, "7E000106008300010120DA0D"),
};
foreach (var v in vectors)
    Check(Convert.ToHexString(TdtProtocol.BuildMosSwitchCommand(v.Charge, v.Enabled, v.Head)) == v.Hex, $"Wrong switch frame: {v}");

var ack = Convert.FromHexString("7E000106008200005AEF0D");
Check(TdtProtocol.TryParseFrame(ack, out var response), "Empty write acknowledgement must parse");
Check(response.Command == 0x82 && TdtProtocol.IsSuccessfulWriteResponse(response), "Wrong write acknowledgement");
Check(!TdtProtocol.IsSuccessfulWriteResponse(new TdtFrame(0x82, [], 0x03)), "Read must not count as write ACK");
Check(!TdtProtocol.IsSuccessfulWriteResponse(new TdtFrame(0x82, [1], 0x06)), "Echoed data must not count as empty ACK");
Check(TdtProtocol.TryParseFrame(Convert.FromHexString("7E00018600820001011CC40D"), out var error), "Error response must parse for diagnostics");
Check(error.Function == 0x86 && !TdtProtocol.IsSuccessfulWriteResponse(error), "Error must not count as success");
var corrupt = (byte[])ack.Clone(); corrupt[8] ^= 1;
Check(!TdtProtocol.TryParseFrame(corrupt, out _), "Corrupt CRC accepted");
for (var length = 0; length < ack.Length; length++)
    Check(!TdtProtocol.TryParseFrame(ack.AsSpan(0, length), out _), $"Truncated frame accepted at length {length}");
Check(!TdtProtocol.TryParseFrame(Convert.FromHexString("7E00010600820000000D"), out _), "10-byte malformed frame accepted");

// Captured from the user's 2026-10-07 log: validates CRC and existing monitoring.
var statusRaw = Convert.FromHexString("7E000103008C0020040CF10CEF0CF20CEC040B6A0B860B790B75C024052C00CD0130008903E80043BEE30D");
var protectionRaw = Convert.FromHexString("7E000103008D0018040000000004000000000000000200000E09000018000000C1120D");
Check(TdtProtocol.TryParseFrame(statusRaw, out var status), "Captured status rejected");
Check(TdtProtocol.TryParseFrame(protectionRaw, out var protection), "Captured protection rejected");
Check(TdtProtocol.TryBuildReading(status, protection, out var reading), "Captured reading rejected");
Check(reading.ChargeMosEnabled && reading.DischargeMosEnabled && reading.PackVoltage == 13.24 && reading.Current == -3.6, "Captured reading changed");
foreach (var (bits, charge, discharge) in new (byte, bool, bool)[] { (0x08, false, false), (0x0a, true, false), (0x0c, false, true), (0x0e, true, true) })
{
    var payload = (byte[])protection.Payload.Clone(); payload[16] = bits;
    Check(TdtProtocol.TryBuildReading(status, new TdtFrame(0x8d, payload), out var r) && r.ChargeMosEnabled == charge && r.DischargeMosEnabled == discharge,
        $"Wrong MOS readback bits 0x{bits:X2}");
}
for (var split = 1; split < ack.Length; split++)
{
    var reader = new TdtFrameReader();
    Check(reader.Add(ack.AsSpan(0, split)).Count == 0, "Premature fragment emission");
    var frames = reader.Add(ack.AsSpan(split));
    Check(frames.Count == 1 && frames[0].SequenceEqual(ack), "Split ACK lost");
}
var coalesced = new TdtFrameReader().Add(ack.Concat(protectionRaw).ToArray());
Check(coalesced.Count == 2, "Coalesced ACK + status lost");
Check(Convert.ToHexString(TdtProtocol.BuildReadCommand(0x8c, 0x1e)) == "1E000103008C0000B1440D", "Known read command changed");
Console.WriteLine($"PASS: {checks} protocol checks (no Bluetooth writes performed).");
Console.WriteLine($"PASS: {await GattRecoveryChecks.RunAsync()} Android GATT recovery checks (simulated callbacks, no Bluetooth writes).");
Console.WriteLine($"PASS: {await ReadAttemptChecks.RunAsync()} poll lifecycle checks (simulated connections, no Bluetooth writes).");
