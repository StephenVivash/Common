using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using WattCycle.Core;

namespace WattCycleApp;

public partial class MainPage : ContentPage
{
	private const string BatteryCountPreferenceKey = "battery-count";
	private const string RememberedBatteriesPreferenceKey = "remembered-batteries";
	private static readonly TimeSpan BatteryGap = TimeSpan.FromSeconds(10);
	private static readonly Color NeutralStateColor = Colors.Gray;
	private static readonly Color LowStateColor = GetResourceColor("Low", Colors.Green);
	private static readonly Color HighStateColor = GetResourceColor("High", Colors.Red);
	private CancellationTokenSource? _loopCts;
	private int _knownBatteryCount;

	public ObservableCollection<BatteryRow> Batteries { get; } = new();

	public MainPage()
	{
		InitializeComponent();
		BindingContext = this;
		BatteriesEntry.TextChanged += OnBatteryCountChanged;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		LoadRememberedRows();
		await StartAsync();
	}

	protected override void OnDisappearing()
	{
		_loopCts?.Cancel();
		base.OnDisappearing();
	}

	private async Task StartAsync()
	{
		if (_loopCts is not null)
		{
			return;
		}

		if (!await EnsureBluetoothPermissionsAsync())
		{
			return;
		}

		_loopCts = new CancellationTokenSource();
		try
		{
			await RunLoopAsync(_loopCts.Token);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_loopCts?.Dispose();
			_loopCts = null;
		}
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken)
	{
		var nextCycleStart = DateTimeOffset.Now;
		while (!cancellationToken.IsCancellationRequested)
		{
			var delayUntilCycle = nextCycleStart - DateTimeOffset.Now;
			if (delayUntilCycle > TimeSpan.Zero)
			{
				await Task.Delay(delayUntilCycle, cancellationToken);
			}

			var cycleStarted = DateTimeOffset.Now;
			var settings = ReadSettings();
			EnsureRememberedBatteryCount(settings.BatteryLimit);
			nextCycleStart = cycleStarted + settings.LoopDelay;

			var advertisements = GetRememberedAdvertisements(settings.BatteryLimit);
			if (advertisements.Count < settings.BatteryLimit)
			{
				await using var scanner = new WattCycleBtClient();
				var scannedAdvertisements = await scanner.FindBatteriesAsync(
					settings.BatteryLimit,
					settings.ScanTimeout,
					advertisement => MainThread.BeginInvokeOnMainThread(() =>
					{
						GetOrAddRow(advertisement);
						SortBatteryRows();
						SaveRememberedRows();
					}),
					cancellationToken);

				advertisements = MergeAdvertisements(advertisements, scannedAdvertisements, settings.BatteryLimit);
			}

			await EnsureRowsAsync(advertisements);
			SaveRememberedRows();

			for (var i = 0; i < advertisements.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var row = await GetOrAddRowAsync(advertisements[i]);
				await ReadBatteryAsync(row, advertisements[i], settings.DataTimeout, cancellationToken);

				if (i < advertisements.Count - 1)
				{
					await Task.Delay(BatteryGap, cancellationToken);
				}
			}
		}
	}

	private async Task ReadBatteryAsync(BatteryRow row, WattCycleDeviceAdvertisement advertisement, TimeSpan timeout, CancellationToken cancellationToken)
	{
		row.Status = "Connecting";
		await using var client = new WattCycleBtClient();
		using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		readCts.CancelAfter(timeout);

		var readingReceived = new TaskCompletionSource<WattCycleBatteryReading>(TaskCreationOptions.RunContinuationsAsynchronously);
		client.InfoMessage += (_, message) => row.Status = message;
		client.DiagnosticMessage += (_, message) =>
		{
			if (message.StartsWith("BMS service discovery failed", StringComparison.Ordinal) ||
				message.StartsWith("Using ", StringComparison.Ordinal) ||
				message.StartsWith("Connected", StringComparison.Ordinal))
			{
				row.Status = message;
			}
		};
		client.ConnectionStatusChanged += (_, status) => row.Status = status;
		client.BatteryReadingReceived += (_, reading) =>
		{
			UpdateRow(row, reading);
			readingReceived.TrySetResult(reading);
		};

		var pollTask = client.ConnectAndPollAsync(advertisement, readCts.Token);
		try
		{
			await readingReceived.Task.WaitAsync(timeout, cancellationToken);
			row.Status = $"Updated {DateTime.Now:HH:mm:ss}";
		}
		catch (TimeoutException)
		{
			if (row.Status is "Connecting" or "Connected" or "Polling" or "Found" or "Waiting")
			{
				row.Status = $"Timed out after {timeout.TotalSeconds:0}s";
			}
		}
		finally
		{
			readCts.Cancel();
			try
			{
				await pollTask;
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				row.Status = $"{ex.GetType().Name}: {ex.Message}";
			}
		}
	}

	private static void UpdateRow(BatteryRow row, WattCycleBatteryReading reading)
	{
		row.StateOfChargeText = $"{reading.StateOfChargePercent}%";
		row.StateOfChargeColor = reading.StateOfChargePercent < 25 ? HighStateColor : LowStateColor;
		row.VoltageText = $"{reading.PackVoltage:0.0}V";
		row.VoltageColor = reading.PackVoltage < 13 ? HighStateColor : LowStateColor;
		row.CurrentText = $"{reading.Current:0.0}A";
		row.CurrentColor = reading.Current >= 0 ? LowStateColor : HighStateColor;
		row.PowerText = $"{reading.PowerWatts:0.0}W";
		row.PowerColor = reading.PowerWatts >= 0 ? LowStateColor : HighStateColor;
		row.ChargeMosEnabled = reading.ChargeMosEnabled;
		row.DischargeMosEnabled = reading.DischargeMosEnabled;
		row.ChargeTextColor = reading.ChargeMosEnabled ? LowStateColor : HighStateColor;
		row.DischargeTextColor = reading.DischargeMosEnabled ? LowStateColor : HighStateColor;
	}

	private Task EnsureRowsAsync(IReadOnlyList<WattCycleDeviceAdvertisement> advertisements)
	{
		return MainThread.InvokeOnMainThreadAsync(() =>
		{
			foreach (var advertisement in advertisements)
			{
				GetOrAddRow(advertisement);
			}

			SortBatteryRows();
		});
	}

	private IReadOnlyList<WattCycleDeviceAdvertisement> GetRememberedAdvertisements(int batteryLimit) =>
		Batteries
			.Where(row => row.BluetoothAddress != 0 && !string.IsNullOrWhiteSpace(row.Name))
			.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(row => row.BluetoothAddress)
			.Take(batteryLimit)
			.Select(row => new WattCycleDeviceAdvertisement(row.BluetoothAddress, row.Name, 0, false))
			.ToArray();

	private static IReadOnlyList<WattCycleDeviceAdvertisement> MergeAdvertisements(
		IReadOnlyList<WattCycleDeviceAdvertisement> remembered,
		IReadOnlyList<WattCycleDeviceAdvertisement> scanned,
		int batteryLimit)
	{
		return remembered
			.Concat(scanned)
			.GroupBy(advertisement => advertisement.BluetoothAddress != 0 ? advertisement.BluetoothAddress.ToString("X") : advertisement.DisplayName, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.Last())
			.OrderBy(advertisement => advertisement.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(advertisement => advertisement.BluetoothAddress)
			.Take(batteryLimit)
			.ToArray();
	}

	private Task<BatteryRow> GetOrAddRowAsync(WattCycleDeviceAdvertisement advertisement)
	{
		return MainThread.InvokeOnMainThreadAsync(() => GetOrAddRow(advertisement));
	}

	private BatteryRow GetOrAddRow(WattCycleDeviceAdvertisement advertisement)
	{
		var row = Batteries.FirstOrDefault(item => item.BluetoothAddress == advertisement.BluetoothAddress);
		if (row is not null)
		{
			row.Name = advertisement.DisplayName;
			return row;
		}

		row = Batteries.FirstOrDefault(item => item.Name.Equals(advertisement.DisplayName, StringComparison.OrdinalIgnoreCase));
		if (row is not null)
		{
			row.BluetoothAddress = advertisement.BluetoothAddress;
			row.Name = advertisement.DisplayName;
			return row;
		}

		row = new BatteryRow(advertisement);
		Batteries.Add(row);
		return row;
	}

	private void LoadRememberedRows()
	{
		var count = ReadInt(BatteriesEntry, 4, 1, 16);
		EnsureRememberedBatteryCount(count);
		var remembered = Preferences.Get(RememberedBatteriesPreferenceKey, string.Empty);
		if (string.IsNullOrWhiteSpace(remembered))
		{
			return;
		}

		Batteries.Clear();
		foreach (var entry in remembered.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = entry.Split('|', 2);
			var address = ulong.TryParse(parts[0], out var parsed) ? parsed : 0;
			var name = parts.Length == 2 ? parts[1] : parts[0];
			if (!string.IsNullOrWhiteSpace(name))
			{
				Batteries.Add(new BatteryRow(address, name));
			}
		}

		SortBatteryRows();
	}

	private void SaveRememberedRows()
	{
		var remembered = Batteries
			.Where(row => !string.IsNullOrWhiteSpace(row.Name))
			.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(row => row.BluetoothAddress)
			.Take(ReadInt(BatteriesEntry, 4, 1, 16))
			.Select(row => $"{row.BluetoothAddress}|{row.Name}");
		Preferences.Set(RememberedBatteriesPreferenceKey, string.Join('\n', remembered));
		Preferences.Set(BatteryCountPreferenceKey, ReadInt(BatteriesEntry, 4, 1, 16));
	}

	private void EnsureRememberedBatteryCount(int batteryCount)
	{
		if (_knownBatteryCount == batteryCount)
		{
			return;
		}

		var storedCount = Preferences.Get(BatteryCountPreferenceKey, batteryCount);
		_knownBatteryCount = batteryCount;
		if (storedCount == batteryCount)
		{
			return;
		}

		Preferences.Remove(RememberedBatteriesPreferenceKey);
		Preferences.Set(BatteryCountPreferenceKey, batteryCount);
		Batteries.Clear();
	}

	private void OnBatteryCountChanged(object? sender, TextChangedEventArgs e)
	{
		var newCount = ReadInt(BatteriesEntry, 4, 1, 16);
		EnsureRememberedBatteryCount(newCount);
	}

	private void SortBatteryRows()
	{
		var sorted = Batteries
			.Select((row, index) => new { Row = row, Index = index })
			.OrderBy(item => item.Row.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.Row.BluetoothAddress)
			.Select(item => item.Row)
			.ToArray();

		for (var targetIndex = 0; targetIndex < sorted.Length; targetIndex++)
		{
			var row = sorted[targetIndex];
			var currentIndex = Batteries.IndexOf(row);
			if (currentIndex >= 0 && currentIndex != targetIndex)
			{
				Batteries.Move(currentIndex, targetIndex);
			}
		}
	}

	private async Task<bool> EnsureBluetoothPermissionsAsync()
	{
#if ANDROID
		var bluetoothStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();
		return bluetoothStatus == PermissionStatus.Granted;
#else
        await Task.CompletedTask;
        return true;
#endif
	}

	private MonitorSettings ReadSettings() =>
		new(
			ReadInt(BatteriesEntry, 4, 1, 16),
			TimeSpan.FromMinutes(ReadDouble(ScanEntry, 2, 0.1, 60)),
			TimeSpan.FromMinutes(ReadDouble(LoopEntry, 5, 0.1, 1440)),
			TimeSpan.FromSeconds(ReadDouble(TimeoutEntry, 30, 1, 600)));

	private static int ReadInt(Entry entry, int defaultValue, int min, int max)
	{
		if (!int.TryParse(entry.Text, out var value))
		{
			value = defaultValue;
		}

		return Math.Clamp(value, min, max);
	}

	private static double ReadDouble(Entry entry, double defaultValue, double min, double max)
	{
		if (!double.TryParse(entry.Text, out var value))
		{
			value = defaultValue;
		}

		return Math.Clamp(value, min, max);
	}

	private static Color GetResourceColor(string key, Color fallback)
	{
		if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
		{
			return color;
		}

		return fallback;
	}

	private sealed record MonitorSettings(int BatteryLimit, TimeSpan ScanTimeout, TimeSpan LoopDelay, TimeSpan DataTimeout);

	public sealed class BatteryRow : INotifyPropertyChanged
	{
		private ulong _bluetoothAddress;
		private string _name;
		private string _stateOfChargeText = "--%";
		private Color _stateOfChargeColor = NeutralStateColor;
		private string _voltageText = "--.-V";
		private Color _voltageColor = NeutralStateColor;
		private string _currentText = "--.-A";
		private Color _currentColor = NeutralStateColor;
		private string _powerText = "--.-W";
		private Color _powerColor = NeutralStateColor;
		private bool _chargeMosEnabled;
		private bool _dischargeMosEnabled;
		private Color _chargeTextColor = NeutralStateColor;
		private Color _dischargeTextColor = NeutralStateColor;
		private string _status = "Waiting";

		public BatteryRow(WattCycleDeviceAdvertisement advertisement)
			: this(advertisement.BluetoothAddress, advertisement.DisplayName)
		{
			Status = "Found";
		}

		public BatteryRow(ulong bluetoothAddress, string name)
		{
			_bluetoothAddress = bluetoothAddress;
			_name = name;
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		public ulong BluetoothAddress
		{
			get => _bluetoothAddress;
			set => SetField(ref _bluetoothAddress, value);
		}

		public string Name
		{
			get => _name;
			set => SetField(ref _name, value);
		}

		public string StateOfChargeText
		{
			get => _stateOfChargeText;
			set => SetField(ref _stateOfChargeText, value);
		}

		public Color StateOfChargeColor
		{
			get => _stateOfChargeColor;
			set => SetField(ref _stateOfChargeColor, value);
		}

		public string VoltageText
		{
			get => _voltageText;
			set => SetField(ref _voltageText, value);
		}

		public Color VoltageColor
		{
			get => _voltageColor;
			set => SetField(ref _voltageColor, value);
		}

		public string CurrentText
		{
			get => _currentText;
			set => SetField(ref _currentText, value);
		}

		public Color CurrentColor
		{
			get => _currentColor;
			set => SetField(ref _currentColor, value);
		}

		public string PowerText
		{
			get => _powerText;
			set => SetField(ref _powerText, value);
		}

		public Color PowerColor
		{
			get => _powerColor;
			set => SetField(ref _powerColor, value);
		}

		public bool ChargeMosEnabled
		{
			get => _chargeMosEnabled;
			set => SetField(ref _chargeMosEnabled, value);
		}

		public bool DischargeMosEnabled
		{
			get => _dischargeMosEnabled;
			set => SetField(ref _dischargeMosEnabled, value);
		}

		public Color ChargeTextColor
		{
			get => _chargeTextColor;
			set => SetField(ref _chargeTextColor, value);
		}

		public Color DischargeTextColor
		{
			get => _dischargeTextColor;
			set => SetField(ref _dischargeTextColor, value);
		}

		public string Status
		{
			get => _status;
			set => SetField(ref _status, value);
		}

		private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value))
			{
				return;
			}

			field = value;
			MainThread.BeginInvokeOnMainThread(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
		}
	}
}
