using System.Text;

namespace Muse.Core;

public static class MusePacketDecoder
{
	public static string DecodeControlText(byte[] bytes)
	{
		if (bytes.Length == 0)
		{
			return "";
		}

		var offset = bytes[0] <= bytes.Length - 1 ? 1 : 0;
		var length = offset == 1 ? bytes[0] : bytes.Length;
		return Encoding.ASCII.GetString(bytes, offset, Math.Min(length, bytes.Length - offset)).Replace("\n", "\\n").Replace("\r", "\\r");
	}

	public static string DecodeEegSummary(byte[] bytes)
	{
		if (!TryDecodeEeg(bytes, out var packet))
		{
			return "EEG packet too short";
		}
		return $"seq={packet.Sequence}, eeg[uV]=[{string.Join(", ", packet.Samples.Select(value => value.ToString("F1")))}]"; // .Take(4)
	}

	public static bool TryDecodeEeg(byte[] bytes, out MuseEegPacket packet)
	{
		packet = default;
		if (bytes.Length < 20)
		{
			return false;
		}

		var sequence = ReadUInt16BigEndian(bytes, 0);
		var samples = new double[12];
		for (var i = 0; i < samples.Length; i++)
		{
			var triplet = 2 + (i / 2 * 3);
			var raw = i % 2 == 0
				? (bytes[triplet] << 4) | (bytes[triplet + 1] >> 4)
				: ((bytes[triplet + 1] & 0x0f) << 8) | bytes[triplet + 2];

			samples[i] = (raw - 2048) * 0.48828125;
		}

		packet = new MuseEegPacket(sequence, samples);
		return true;
	}

	public static string DecodeImuSummary(byte[] bytes)
	{
		if (!TryDecodeImu(bytes, out var packet))
		{
			return "IMU packet too short";
		}

		return $"seq={packet.Sequence}, xyzSamples=[{string.Join(", ", packet.Samples)}]";
	}

	public static bool TryDecodeImu(byte[] bytes, out MuseImuPacket packet)
	{
		packet = default;
		if (bytes.Length < 20)
		{
			return false;
		}

		var sequence = ReadUInt16BigEndian(bytes, 0);
		var values = new short[9];
		for (var i = 0; i < values.Length; i++)
		{
			values[i] = unchecked((short)ReadUInt16BigEndian(bytes, 2 + i * 2));
		}

		packet = new MuseImuPacket(sequence, values);
		return true;
	}

	public static bool TryDecodePpg(byte[] bytes, out MusePpgPacket packet)
	{
		packet = default;
		if (bytes.Length < 20)
		{
			return false;
		}

		var sequence = ReadUInt16BigEndian(bytes, 0);
		var values = new int[6];
		for (var i = 0; i < values.Length; i++)
		{
			var offset = 2 + i * 3;
			values[i] = (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
		}

		packet = new MusePpgPacket(sequence, values);
		return true;
	}

	public static string DecodeTelemetrySummary(byte[] bytes)
	{
		if (bytes.Length < 10)
		{
			return "telemetry packet too short";
		}

		var values = Enumerable.Range(0, 5).Select(i => ReadUInt16BigEndian(bytes, i * 2)).ToArray();
		var batteryPercentEstimate = values[1] / 512.0;
		return $"fields=[{string.Join(", ", values)}], batteryEstimate={batteryPercentEstimate:F1}%";
	}

	public static ushort ReadUInt16BigEndian(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
}
