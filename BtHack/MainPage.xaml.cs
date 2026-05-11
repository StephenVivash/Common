using Muse.Core;
using WattCycle.Core;

namespace BtHack;

public partial class MainPage : ContentPage
{
	internal bool debug = true;

	private MuseBtClient? _museClient;
	private WattCycleBtClient? _wattCycleClient;
	private CancellationTokenSource? _streamingCts;
	private bool _bandHeaderPrinted;

	public MainPage()
	{
		InitializeComponent();
		BluetoothInterfacePicker.SelectedIndex = 1;
    }

	private async void OnStartClicked(object? sender, EventArgs e)
	{
		if (!await EnsureBluetoothPermissionsAsync())
		{
			return;
		}

		Log.IsStartEnabled = false;
		Log.IsStopEnabled = true;
		Log.StatusText = "Scanning";
		_bandHeaderPrinted = false;

		var selectedInterface = GetSelectedBluetoothInterface();
		var debugLogPath = Log.StartFileLog(selectedInterface == BluetoothInterface.WattCycle ? "WattCycleBtDebug" : "MuseBtDebug");
		LogDebug(debugLogPath is null ? "Full debug log unavailable." : $"Full debug log: {debugLogPath}");

		_streamingCts = new CancellationTokenSource();

		try
		{
			if (selectedInterface == BluetoothInterface.WattCycle)
			{
				await RunWattCycleAsync(_streamingCts.Token);
			}
			else
			{
				await RunMuseAsync(_streamingCts.Token);
			}
		}
		catch (OperationCanceledException)
		{
			LogDebug("Stopped.");
		}
		catch (Exception ex)
		{
			LogDebug($"{ex.GetType().Name}: {ex.Message}");
		}
		finally
		{
			await StopClientAsync();
			var savedLogPath = Log.LogFilePath;
			if (!string.IsNullOrWhiteSpace(savedLogPath))
			{
				LogDebug($"Debug log saved to {savedLogPath}");
			}

			Log.StopFileLog();
			Log.StatusText = "Idle";
			Log.IsStartEnabled = true;
			Log.IsStopEnabled = false;
		}
	}

	private void OnStopClicked(object? sender, EventArgs e)
	{
		_streamingCts?.Cancel();
		LogDebug("Stopping...");
		Log.StatusText = "Stopping";
		Log.IsStopEnabled = false;
	}

	private async Task RunMuseAsync(CancellationToken cancellationToken)
	{
		_museClient = new MuseBtClient();
		_museClient.InfoMessage += (_, message) => LogDebug(message);
		_museClient.DiagnosticMessage += (_, message) => LogDebug(message);
		_museClient.ConnectionStatusChanged += (_, status) => MainThread.BeginInvokeOnMainThread(() => Log.StatusText = status);
		_museClient.EegPacketDiagnostic += (_, diagnostic) => LogEegPacketDiagnostic(diagnostic);
		_museClient.NotificationReceived += (_, notification) =>
		{
			if (debug && (notification.Count <= 3 || notification.Count % 50 == 0))
			{
				LogNotification(notification);
			}
		};
		_museClient.BandPowersCalculated += (_, reading) => PrintBandSummary(reading);

		LogDebug("Scanning for Muse headset...");
		var advertisement = await _museClient.FindMuseAsync(TimeSpan.FromSeconds(30), cancellationToken);
		if (advertisement is null)
		{
			LogDebug("No Muse advertisement found.");
			Log.StatusText = "Idle";
			return;
		}

		Log.StatusText = $"Connecting to {advertisement.DisplayName}";
		LogDebug($"Found {advertisement.DisplayName} at 0x{advertisement.BluetoothAddress:X}.");
		await _museClient.ConnectAndStreamAsync(advertisement, cancellationToken);
	}

	private async Task RunWattCycleAsync(CancellationToken cancellationToken)
	{
		_wattCycleClient = new WattCycleBtClient();
		_wattCycleClient.InfoMessage += (_, message) => LogDebug(message);
		_wattCycleClient.DiagnosticMessage += (_, message) => LogDebug(message);
		_wattCycleClient.ConnectionStatusChanged += (_, status) => MainThread.BeginInvokeOnMainThread(() => Log.StatusText = status);
		_wattCycleClient.BatteryReadingReceived += (_, reading) => LogWattCycleReading(reading);

		LogDebug("Scanning for WattCycle battery...");
		var advertisement = await _wattCycleClient.FindBatteryAsync(TimeSpan.FromSeconds(30), cancellationToken);
		if (advertisement is null)
		{
			LogDebug("No WattCycle battery advertisement found.");
			Log.StatusText = "Idle";
			return;
		}

		Log.StatusText = $"Connecting to {advertisement.DisplayName}";
		LogDebug($"Found {advertisement.DisplayName} at 0x{advertisement.BluetoothAddress:X}.");
		await _wattCycleClient.ConnectAndPollAsync(advertisement, cancellationToken);
	}

	private async Task StopClientAsync()
	{
		_streamingCts?.Cancel();
		_streamingCts?.Dispose();
		_streamingCts = null;

		if (_museClient is not null)
		{
			await _museClient.DisposeAsync();
			_museClient = null;
		}

		if (_wattCycleClient is not null)
		{
			await _wattCycleClient.DisposeAsync();
			_wattCycleClient = null;
		}
	}

	private void LogWattCycleReading(WattCycleBatteryReading reading)
	{
		var cells = reading.CellVoltages.Count == 0 ? "n/a" : string.Join(", ", reading.CellVoltages.Select((v, i) => $"C{i + 1}={v:F3}V"));
		var temps = reading.TemperaturesCelsius.Count == 0 ? "n/a" : string.Join(", ", reading.TemperaturesCelsius.Select((t, i) => $"T{i + 1}={t:F1}C"));
		Log.Add(
			$"WattCycle {reading.PackVoltage:F2}V {reading.Current:F2}A {reading.PowerWatts:F1}W SOC={reading.StateOfChargePercent}% " +
			$"cap={reading.RemainingCapacityAh:F2}/{reading.NominalCapacityAh:F2}Ah cycles={reading.CycleCount} " +
			$"chargeMos={reading.ChargeMosEnabled} dischargeMos={reading.DischargeMosEnabled} protection=0x{reading.ProtectionStatus:X4} cells=[{cells}] temps=[{temps}]");
	}

	private void LogNotification(MuseNotification notification)
	{
		bool hex = false;
		var prefix = $"{notification.Name} #{notification.Count}: {notification.Data.Length} bytes";
		switch (notification.Kind)
		{
			case MuseSensorKind.Control:
				Log.Add($"{prefix}, text={MusePacketDecoder.DecodeControlText(notification.Data)}" + (hex ? $", hex={ToHex(notification.Data)}" : ""));
				break;
			case MuseSensorKind.Eeg:
				Log.Add($"{prefix}, {MusePacketDecoder.DecodeEegSummary(notification.Data)}" + (hex ? $", hex={ToHex(notification.Data)}" : ""));
				break;
			case MuseSensorKind.Imu:
				Log.Add($"{prefix}, {MusePacketDecoder.DecodeImuSummary(notification.Data)}" + (hex ? $", hex={ToHex(notification.Data)}" : ""));
				break;
			case MuseSensorKind.Telemetry:
				Log.Add($"{prefix}, {MusePacketDecoder.DecodeTelemetrySummary(notification.Data)}" + (hex ? $", hex={ToHex(notification.Data)}" : ""));
				break;
			default:
				Log.Add($"{prefix}, hex={ToHex(notification.Data)}");
				break;
		}
	}

	private void PrintBandSummary(MuseBandPowerReading reading)
	{
		PrintBandHeaderOnce();
		Log.Add(
			$"{FormatSensorName(reading.SensorName),-6} | " +

			$"{FormatBandCell(reading.Bands.DeltaDb, reading.Bands.DeltaAbsolute)} | " +

			$"{FormatBandCell(reading.Bands.ThetaDb, reading.Bands.ThetaAbsolute)} | " +

			$"{FormatBandCell(reading.Bands.AlphaDb, reading.Bands.AlphaAbsolute)} | " +

			$"{FormatBandCell(reading.Bands.BetaDb, reading.Bands.BetaAbsolute)} | " +

			$"{FormatBandCell(reading.Bands.GammaDb, reading.Bands.GammaAbsolute)} |");
	}

	private void LogEegPacketDiagnostic(MuseEegPacketDiagnostic diagnostic)
	{
		if (!debug)
		{
			return;
		}

		bool shouldLog = diagnostic.Count <= 3 ||
			diagnostic.Count % 50 == 0 ||
			diagnostic.LargeSequenceJump ||
			(diagnostic.IntervalMilliseconds.HasValue && diagnostic.IntervalMilliseconds.Value > 250);

		if (!shouldLog)
		{
			return;
		}

		var interval = diagnostic.IntervalMilliseconds.HasValue ? $"{diagnostic.IntervalMilliseconds.Value,6:F1}ms" : "     -";
		var sequenceDelta = diagnostic.SequenceDelta.HasValue ? $"{diagnostic.SequenceDelta.Value,2}" : " -";
		var sequenceJump = diagnostic.LargeSequenceJump ? "yes" : " no";
		Log.Add(
			$"{FormatSensorName(diagnostic.SensorName),-6} pkt #{diagnostic.Count,5} " +
			$"seq={diagnostic.Sequence,5} dSeq={sequenceDelta} jump={sequenceJump} totalJump={diagnostic.TotalLargeSequenceJumps,3} " +
			$"dt={interval} uV[min={diagnostic.MinMicrovolts,7:F1}, max={diagnostic.MaxMicrovolts,7:F1}, " +
			$"meanAbs={diagnostic.MeanAbsMicrovolts,6:F1}, rms={diagnostic.RmsMicrovolts,6:F1}]");
	}

	private void PrintBandHeaderOnce()
	{
		if (_bandHeaderPrinted)
		{
			return;
		}

		_bandHeaderPrinted = true;
		Log.Add("Sensor | D dB  Abs   | T dB  Abs   | A dB  Abs   | B dB  Abs   | G dB  Abs   |");
	}

	private void LogDebug(string message)
	{
		if (debug)
		{
			Log.Add(message);
		}
	}

	private static string FormatSensorName(string name) => name.StartsWith("EEG ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

	private static string FormatBandCell(double db, double osc) => $"{db,4:F1} {osc,6:F3}";

	private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes);

	private BluetoothInterface GetSelectedBluetoothInterface() =>
		BluetoothInterfacePicker.SelectedIndex == 1 ? BluetoothInterface.WattCycle : BluetoothInterface.Muse;

	private async Task<bool> EnsureBluetoothPermissionsAsync()
	{
#if ANDROID
		var bluetoothStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();
		if (bluetoothStatus != PermissionStatus.Granted)
		{
			LogDebug("Bluetooth permission was not granted.");
			return false;
		}

		return true;
#else
		return await Task.FromResult(true);
#endif
	}

	private enum BluetoothInterface
	{
		Muse,
		WattCycle
	}
}
