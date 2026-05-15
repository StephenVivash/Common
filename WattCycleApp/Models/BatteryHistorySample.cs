using WattCycle.Core;

namespace WattCycleApp.Models;

public sealed record BatteryHistorySample(
	DateTimeOffset Timestamp,
	ulong BluetoothAddress,
	string BatteryName,
	double PackVoltage,
	double Current,
	double PowerWatts,
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
	public static BatteryHistorySample FromReading(
		ulong bluetoothAddress,
		string batteryName,
		WattCycleBatteryReading reading) =>
		new(
			reading.Timestamp,
			bluetoothAddress,
			batteryName,
			reading.PackVoltage,
			reading.Current,
			reading.PowerWatts,
			reading.RemainingCapacityAh,
			reading.NominalCapacityAh,
			reading.StateOfChargePercent,
			reading.CycleCount,
			reading.ChargeMosEnabled,
			reading.DischargeMosEnabled,
			reading.CellVoltages.ToArray(),
			reading.TemperaturesCelsius.ToArray(),
			reading.ProtectionStatus);
}
