using System.Buffers.Binary;

namespace WattCycle.Core;

internal static class JbdProtocol
{
	private const byte ReadCommand = 0xa5;
	private const byte WriteCommand = 0x5a;

	public static byte[] BuildReadCommand(byte register)
	{
		Span<byte> frame = stackalloc byte[7];
		frame[0] = 0xdd;
		frame[1] = ReadCommand;
		frame[2] = register;
		frame[3] = 0x00;
		var checksum = CalculateChecksum(frame[2..4]);
		BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], checksum);
		frame[6] = 0x77;
		return frame.ToArray();
	}

	public static byte[] BuildWriteCommand(byte register, ReadOnlySpan<byte> payload)
	{
		if (payload.Length > byte.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(payload), "JBD write payload is too large.");
		}

		var frame = new byte[payload.Length + 7];
		frame[0] = 0xdd;
		frame[1] = WriteCommand;
		frame[2] = register;
		frame[3] = (byte)payload.Length;
		payload.CopyTo(frame.AsSpan(4));
		var checksum = CalculateChecksum(frame.AsSpan(2, payload.Length + 2));
		BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4 + payload.Length, 2), checksum);
		frame[^1] = 0x77;
		return frame;
	}

	public static bool TryParseFrame(ReadOnlySpan<byte> frame, out JbdFrame parsed)
	{
		parsed = new JbdFrame(0, 0, Array.Empty<byte>());
		if (frame.Length < 7 || frame[0] != 0xdd || frame[^1] != 0x77)
		{
			return false;
		}

		var register = frame[1];
		var status = frame[2];
		var length = frame[3];
		if (frame.Length != length + 7)
		{
			return false;
		}

		var payload = frame.Slice(4, length).ToArray();
		var expected = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4 + length, 2));
		var actual = CalculateChecksum(frame.Slice(1, length + 3));
		if (expected != actual)
		{
			return false;
		}

		parsed = new JbdFrame(register, status, payload);
		return true;
	}

	public static bool TryParseBasicInfo(JbdFrame frame, out BasicInfo basicInfo)
	{
		basicInfo = new BasicInfo(0, 0, 0, 0, 0, 0, 0, false, false, 0, Array.Empty<double>());
		var payload = frame.Payload;
		if (frame.Register != WattCycleBluetoothConstants.BasicInfoRegister || frame.Status != 0 || payload.Length < 23)
		{
			return false;
		}

		var temperatureCount = payload[22];
		var expectedLength = 23 + temperatureCount * 2;
		if (payload.Length < expectedLength)
		{
			return false;
		}

		var temperatures = new double[temperatureCount];
		for (var i = 0; i < temperatureCount; i++)
		{
			temperatures[i] = (ReadUInt16(payload, 23 + i * 2) - 2731) / 10.0;
		}

		basicInfo = new BasicInfo(
			PackVoltage: ReadUInt16(payload, 0) / 100.0,
			Current: ReadInt16(payload, 2) / 100.0,
			RemainingCapacityAh: ReadUInt16(payload, 4) / 100.0,
			NominalCapacityAh: ReadUInt16(payload, 6) / 100.0,
			CycleCount: ReadUInt16(payload, 8),
			ProtectionStatus: ReadUInt16(payload, 16),
			StateOfChargePercent: payload[19],
			ChargeMosEnabled: (payload[20] & 0x01) != 0,
			DischargeMosEnabled: (payload[20] & 0x02) != 0,
			CellCount: payload[21],
			TemperaturesCelsius: temperatures);
		return true;
	}

	public static bool TryParseCellVoltages(JbdFrame frame, out IReadOnlyList<double> cellVoltages)
	{
		cellVoltages = Array.Empty<double>();
		var payload = frame.Payload;
		if (frame.Register != WattCycleBluetoothConstants.CellVoltagesRegister || frame.Status != 0 || payload.Length < 2 || payload.Length % 2 != 0)
		{
			return false;
		}

		var voltages = new double[payload.Length / 2];
		for (var i = 0; i < voltages.Length; i++)
		{
			voltages[i] = ReadUInt16(payload, i * 2) / 1000.0;
		}

		cellVoltages = voltages;
		return true;
	}

	private static ushort CalculateChecksum(ReadOnlySpan<byte> bytes)
	{
		var sum = 0;
		foreach (var value in bytes)
		{
			sum += value;
		}

		return (ushort)(0x10000 - sum);
	}

	private static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

	private static short ReadInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2));
}

internal sealed record JbdFrame(byte Register, byte Status, byte[] Payload);

internal sealed record BasicInfo(
	double PackVoltage,
	double Current,
	double RemainingCapacityAh,
	double NominalCapacityAh,
	int CycleCount,
	ushort ProtectionStatus,
	int StateOfChargePercent,
	bool ChargeMosEnabled,
	bool DischargeMosEnabled,
	int CellCount,
	IReadOnlyList<double> TemperaturesCelsius);
