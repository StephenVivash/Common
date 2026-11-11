namespace Muse.Core;

public enum MuseSensorKind
{
	Control,
	Eeg,
	Imu,
	Telemetry,
	Raw
}

public sealed record MuseDeviceAdvertisement(ulong BluetoothAddress, string Name, short Rssi, bool ServiceAdvertised)
{
    // Apple exposes a peripheral UUID rather than a MAC address. BluetoothAddress
    // is unused (zero) for Apple advertisements; always connect using this UUID.
    public Guid? PeripheralIdentifier { get; init; }

    public string DeviceIdentifier => PeripheralIdentifier?.ToString() ?? $"0x{BluetoothAddress:X}";

    public static MuseDeviceAdvertisement FromPeripheral(Guid identifier, string name, short rssi, bool serviceAdvertised) =>
        new(0, name, rssi, serviceAdvertised) { PeripheralIdentifier = identifier };

	public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Muse headset" : Name;
}

public sealed record MuseSensorDefinition(string Name, Guid Uuid, MuseSensorKind Kind);

public sealed record MuseNotification(string Name, Guid Uuid, MuseSensorKind Kind, byte[] Data, long Count);

public readonly record struct MuseEegPacket(ushort Sequence, double[] Samples);

public readonly record struct MuseImuPacket(ushort Sequence, short[] Samples);

public readonly record struct MusePpgPacket(ushort Sequence, int[] Samples);

public readonly record struct MuseBandPowers(
	double DeltaAbsolute,
	double ThetaAbsolute,
	double AlphaAbsolute,
	double BetaAbsolute,
	double GammaAbsolute)
{
	public double DeltaDb => DeltaAbsolute * 10.0;

	public double ThetaDb => ThetaAbsolute * 10.0;

	public double AlphaDb => AlphaAbsolute * 10.0;

	public double BetaDb => BetaAbsolute * 10.0;

	public double GammaDb => GammaAbsolute * 10.0;
}

public sealed record MuseBandPowerReading(string SensorName, MuseBandPowers Bands);
