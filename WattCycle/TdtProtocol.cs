using System.Buffers.Binary;
using System.Text;

namespace WattCycle.Core;

internal static class TdtProtocol
{
	// Verified against Wattcycle_11_11_betaV4.apk: WattBleProtocolRepository.
	public const byte ChargeSwitchCommand = 0x82;
	public const byte DischargeSwitchCommand = 0x83;
	public const byte ReadFunction = 0x03;
	public const byte WriteFunction = 0x06;
	public const byte StatusCommand = 0x8c;
	public const byte ProtectionCommand = 0x8d;
	public static readonly byte[] UnlockCommand = Encoding.ASCII.GetBytes("HiLink");

	public static byte[] BuildReadCommand(byte command, byte commandHead)
	{
		Span<byte> frame = stackalloc byte[11];
		frame[0] = commandHead;
		frame[1] = 0x00;
		frame[2] = 0x01;
		frame[3] = 0x03;
		frame[4] = 0x00;
		frame[5] = command;
		frame[6] = 0x00;
		frame[7] = 0x00;
		var crc = CrcModbus(frame[..8]);
		BinaryPrimitives.WriteUInt16BigEndian(frame[8..10], crc);
		frame[10] = 0x0d;
		return frame.ToArray();
	}

	public static byte[] BuildMosSwitchCommand(bool chargeSwitch, bool enabled, byte commandHead)
	{
		// The switches are separate one-byte writes, not a combined JBD MOS mask.
		var frame = new byte[12];
		frame[0] = commandHead;
		frame[2] = 0x01;
		frame[3] = WriteFunction;
		frame[5] = chargeSwitch ? ChargeSwitchCommand : DischargeSwitchCommand;
		frame[7] = 0x01;
		frame[8] = enabled ? (byte)1 : (byte)0;
		BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9, 2), CrcModbus(frame.AsSpan(0, 9)));
		frame[11] = 0x0d;
		return frame;
	}

	public static bool IsSuccessfulWriteResponse(TdtFrame frame) =>
		frame.Function == WriteFunction && frame.Payload.Length == 0;

	public static bool TryParseFrame(ReadOnlySpan<byte> frame, out TdtFrame parsed)
	{
		parsed = new TdtFrame(0, Array.Empty<byte>());
		if (frame.Length < 11 || frame[0] != 0x7e || frame[^1] != 0x0d)
		{
			return false;
		}

		if (frame[1] is not (0x00 or 0x04) || frame[2] != 0x01 || frame[4] != 0x00)
		{
			return false;
		}

		var dataLength = BinaryPrimitives.ReadUInt16BigEndian(frame[6..8]);
		if (frame.Length != dataLength + 11)
		{
			return false;
		}

		var expected = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(frame.Length - 3, 2));
		var actual = CrcModbus(frame[..^3]);
		if (expected != actual)
		{
			return false;
		}

		parsed = new TdtFrame(frame[5], frame.Slice(8, dataLength).ToArray(), frame[3]);
		return true;
	}

	public static bool TryBuildReading(TdtFrame statusFrame, TdtFrame protectionFrame, out WattCycleBatteryReading reading)
	{
		reading = new WattCycleBatteryReading(DateTimeOffset.Now, 0, 0, 0, 0, 0, 0, false, false, [], [], 0);
		if (statusFrame.Command != StatusCommand || protectionFrame.Command != ProtectionCommand || statusFrame.Payload.Length < 24)
		{
			return false;
		}

		var status = statusFrame.Payload;
		var cellCount = status[0];
		var cellStart = 1;
		var tempCountOffset = cellStart + cellCount * 2;
		if (status.Length <= tempCountOffset)
		{
			return false;
		}

		var tempCount = status[tempCountOffset];
		var tempStart = tempCountOffset + 1;
		var dataStart = tempStart + tempCount * 2;
		if (status.Length < dataStart + 14)
		{
			return false;
		}

		var cells = new double[cellCount];
		for (var i = 0; i < cells.Length; i++)
		{
			cells[i] = ReadUInt16(status, cellStart + i * 2) / 1000.0;
		}

		var temperatures = new double[tempCount];
		for (var i = 0; i < temperatures.Length; i++)
		{
			temperatures[i] = (ReadUInt16(status, tempStart + i * 2) - 2731) / 10.0;
		}

		var currentRaw = ReadUInt16(status, dataStart);
		var current = (currentRaw & 0x3fff) / 10.0 * ((currentRaw & 0x8000) != 0 ? -1 : 1);

		if (!TryReadProtectionFields(protectionFrame.Payload, cellCount, tempCount, out var protectionStatus, out var mosfets))
		{
			return false;
		}

		reading = new WattCycleBatteryReading(
			DateTimeOffset.Now,
			ReadUInt16(status, dataStart + 2) / 100.0,
			current,
			ReadUInt16(status, dataStart + 4) / 10.0,
			ReadUInt16(status, dataStart + 10) / 10.0,
			ReadUInt16(status, dataStart + 12),
			ReadUInt16(status, dataStart + 6),
			(mosfets & 0x02) != 0,
			(mosfets & 0x04) != 0,
			cells,
			temperatures,
			protectionStatus);
		return true;
	}

	public static string DescribeStatusFields(TdtFrame statusFrame, TdtFrame protectionFrame)
	{
		if (statusFrame.Command != StatusCommand || statusFrame.Payload.Length < 24)
		{
			return "TDT status fields unavailable.";
		}

		var status = statusFrame.Payload;
		var cellCount = status[0];
		var tempCountOffset = 1 + cellCount * 2;
		if (status.Length <= tempCountOffset)
		{
			return "TDT status fields unavailable.";
		}

		var tempCount = status[tempCountOffset];
		var dataStart = tempCountOffset + 1 + tempCount * 2;
		if (status.Length < dataStart + 14)
		{
			return "TDT status fields unavailable.";
		}

		var currentRaw = ReadUInt16(status, dataStart);
		var current = (currentRaw & 0x3fff) / 10.0 * ((currentRaw & 0x8000) != 0 ? -1 : 1);
		var mosfets = protectionFrame.Command == ProtectionCommand &&
			TryReadProtectionFields(protectionFrame.Payload, cellCount, tempCount, out _, out var parsedMosfets)
			? parsedMosfets
			: (byte)0;

		return
			$"TDT fields rawCurrent=0x{currentRaw:X4} decodedCurrent={current:F1}A " +
			$"voltageRaw=0x{ReadUInt16(status, dataStart + 2):X4} remainingRaw=0x{ReadUInt16(status, dataStart + 4):X4} " +
			$"cyclesRaw=0x{ReadUInt16(status, dataStart + 6):X4} sohRaw=0x{ReadUInt16(status, dataStart + 8):X4} nominalRaw=0x{ReadUInt16(status, dataStart + 10):X4} " +
			$"socRaw=0x{ReadUInt16(status, dataStart + 12):X4} mosRaw=0x{mosfets:X2}";
	}

	public static int ExpectedFrameLength(ReadOnlySpan<byte> buffer)
	{
		if (buffer.Length < 8)
		{
			return 0;
		}

		return BinaryPrimitives.ReadUInt16BigEndian(buffer[6..8]) + 11;
	}

	private static bool TryReadProtectionFields(byte[] payload, int cellCount, int tempCount, out ushort protectionStatus, out byte mosfets)
	{
		protectionStatus = 0;
		mosfets = 0;
		var idx = cellCount + tempCount;
		var protectionOffset = idx + 6;
		var mosfetOffset = idx + 8;
		if (payload.Length <= mosfetOffset)
		{
			return false;
		}

		protectionStatus = ReadUInt16(payload, protectionOffset);
		mosfets = payload[mosfetOffset];
		return true;
	}

	private static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

	private static ushort CrcModbus(ReadOnlySpan<byte> bytes)
	{
		var crc = 0xffff;
		foreach (var value in bytes)
		{
			crc ^= value;
			for (var i = 0; i < 8; i++)
			{
				crc = (crc & 0x0001) != 0 ? (crc >> 1) ^ 0xa001 : crc >> 1;
			}
		}

		return (ushort)crc;
	}
}

internal sealed record TdtFrame(byte Command, byte[] Payload, byte Function = TdtProtocol.ReadFunction);
