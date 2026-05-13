namespace Muse.Core;

public static class MuseBluetoothConstants
{
	public const int EegSampleRate = 256;
	public const int DefaultBandWindowSamples = 256;
	public const int DefaultBandHopSamples = 26;
	public const bool EnableBandPowerNotch = true;
	public const double BandPowerNotchFrequencyHz = 50.0;
	public const double BandPowerNotchQ = 35.0;
	public const double GammaSpikeLimitMedianMultiplier = 2.0;
	public static readonly TimeSpan BandPowerPublishInterval = TimeSpan.FromMilliseconds(100);
	public static readonly TimeSpan AutoReconnectInitialDelay = TimeSpan.FromSeconds(2);
	public static readonly TimeSpan AutoReconnectMaxDelay = TimeSpan.FromSeconds(20);
	public static readonly TimeSpan NotificationStallReconnectTimeout = TimeSpan.FromSeconds(8);

	public static readonly Guid MuseServiceUuid = Guid.Parse("0000fe8d-0000-1000-8000-00805f9b34fb");
	public static readonly Guid ControlUuid = MuseUuid(0x0001);

	public static readonly IReadOnlyList<MuseSensorDefinition> Sensors =
	[
		new("EEG TP9", MuseUuid(0x0003), MuseSensorKind.Eeg),
		new("EEG AF7", MuseUuid(0x0004), MuseSensorKind.Eeg),
		new("EEG AF8", MuseUuid(0x0005), MuseSensorKind.Eeg),
		new("EEG TP10", MuseUuid(0x0006), MuseSensorKind.Eeg),
		new("EEG AUX", MuseUuid(0x0007), MuseSensorKind.Eeg),
		new("Gyroscope", MuseUuid(0x0009), MuseSensorKind.Imu),
		new("Accelerometer", MuseUuid(0x000a), MuseSensorKind.Imu),
		new("Telemetry", MuseUuid(0x000b), MuseSensorKind.Telemetry),
		new("PPG ambient", MuseUuid(0x000f), MuseSensorKind.Raw),
		new("PPG IR", MuseUuid(0x0010), MuseSensorKind.Raw),
		new("PPG red", MuseUuid(0x0011), MuseSensorKind.Raw),
		new("Athena data", MuseUuid(0x0013), MuseSensorKind.Raw),
	];

	public static Guid MuseUuid(int shortId) => Guid.Parse($"273e{shortId:x4}-4c4d-454d-96be-f03bac821358");
}
