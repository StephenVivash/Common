using System.Text.Json;

using WattCycle.Core;
using WattCycleApp.Models;

namespace WattCycleApp.Services;

public sealed class BatteryHistoryStore
{
	private const int MaxSamplesPerBattery = 2880;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly List<BatteryHistorySample> _samples = new();
	private bool _loaded;

	public static BatteryHistoryStore Default { get; } = new();

	public event EventHandler<BatteryHistorySample>? SampleAdded;

	private static string HistoryFilePath => Path.Combine(FileSystem.AppDataDirectory, "battery-history.json");

	public async Task AddSampleAsync(ulong bluetoothAddress, string batteryName, WattCycleBatteryReading reading)
	{
		var sample = BatteryHistorySample.FromReading(bluetoothAddress, batteryName, reading);

		await _gate.WaitAsync();
		try
		{
			await LoadCoreAsync();
			_samples.Add(sample);
			PruneBatterySamples(bluetoothAddress, batteryName);
			await SaveCoreAsync();
		}
		finally
		{
			_gate.Release();
		}

		SampleAdded?.Invoke(this, sample);
	}

	public async Task<IReadOnlyList<BatteryHistorySample>> GetSamplesAsync()
	{
		await _gate.WaitAsync();
		try
		{
			await LoadCoreAsync();
			return _samples
				.OrderBy(sample => sample.Timestamp)
				.ToArray();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<BatteryHistorySample>> GetSamplesAsync(ulong bluetoothAddress, string? batteryName = null)
	{
		await _gate.WaitAsync();
		try
		{
			await LoadCoreAsync();
			return _samples
				.Where(sample => IsSameBattery(sample, bluetoothAddress, batteryName))
				.OrderBy(sample => sample.Timestamp)
				.ToArray();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task LoadCoreAsync()
	{
		if (_loaded)
		{
			return;
		}

		_loaded = true;
		if (!File.Exists(HistoryFilePath))
		{
			return;
		}

		try
		{
			await using var stream = File.OpenRead(HistoryFilePath);
			var samples = await JsonSerializer.DeserializeAsync<List<BatteryHistorySample>>(stream, JsonOptions);
			if (samples is not null)
			{
				_samples.AddRange(samples);
			}
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			_samples.Clear();
		}
	}

	private async Task SaveCoreAsync()
	{
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		await using var stream = File.Create(HistoryFilePath);
		await JsonSerializer.SerializeAsync(stream, _samples, JsonOptions);
	}

	private void PruneBatterySamples(ulong bluetoothAddress, string batteryName)
	{
		var batterySamples = _samples
			.Where(sample => IsSameBattery(sample, bluetoothAddress, batteryName))
			.OrderByDescending(sample => sample.Timestamp)
			.Skip(MaxSamplesPerBattery)
			.ToArray();

		foreach (var sample in batterySamples)
		{
			_samples.Remove(sample);
		}
	}

	private static bool IsSameBattery(BatteryHistorySample sample, ulong bluetoothAddress, string? batteryName)
	{
		if (bluetoothAddress != 0)
		{
			return sample.BluetoothAddress == bluetoothAddress;
		}

		return !string.IsNullOrWhiteSpace(batteryName) &&
			sample.BatteryName.Equals(batteryName, StringComparison.OrdinalIgnoreCase);
	}
}
