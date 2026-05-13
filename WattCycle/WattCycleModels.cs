namespace WattCycle.Core;

public sealed record WattCycleDeviceAdvertisement(ulong BluetoothAddress, string Name, short Rssi, bool ServiceAdvertised)
{
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
