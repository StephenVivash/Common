using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WattCycle.Core;

public sealed record WattCycleDeviceAdvertisement(ulong BluetoothAddress, string Name, short Rssi, bool ServiceAdvertised)
{
    // Apple uses a per-host peripheral UUID, never a Bluetooth MAC address.
    public Guid? PeripheralIdentifier { get; init; }
    public static WattCycleDeviceAdvertisement FromPeripheral(Guid identifier, string name, short rssi, bool serviceAdvertised)
    {
        var hash = SHA256.HashData(identifier.ToByteArray());
        var key = BinaryPrimitives.ReadUInt64BigEndian(hash) | (1UL << 63);
        return new(key, name, rssi, serviceAdvertised) { PeripheralIdentifier = identifier };
    }
	public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "WattCycle battery" : Name;
}

public sealed record WattCycleBatteryReading(
	DateTimeOffset Timestamp,
	double PackVoltage,
	double Current,
	double RemainingCapacityAh,
	double NominalCapacityAh,
	int StateOfChargePercent,
	int CycleCount,
	bool ChargeMosEnabled,
	bool DischargeMosEnabled,
	IReadOnlyList<double> CellVoltages,
	IReadOnlyList<double> TemperaturesCelsius,
	ushort ProtectionStatus)
{
	public double PowerWatts => PackVoltage * Current;
}

public sealed record WattCycleMosControlResult(
	WattCycleBatteryReading Reading,
	bool ChargeMatches,
	bool DischargeMatches)
{
	public bool Matches => ChargeMatches && DischargeMatches;
}
