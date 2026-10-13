using System.Diagnostics;

namespace WattCycleApp.Services;

internal sealed class BatteryControlLog
{
	private readonly object _gate = new();
	private readonly Stopwatch _elapsed = Stopwatch.StartNew();
	public string? FilePath { get; }

	public BatteryControlLog(string batteryName, ulong address)
	{
		try
		{
			FilePath = Path.Combine(FileSystem.AppDataDirectory,
				$"WattCycleControl-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
			Directory.CreateDirectory(FileSystem.AppDataDirectory);
			File.WriteAllText(FilePath, string.Empty);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Control file logging unavailable: {ex}");
			FilePath = null;
		}
		Write($"Battery={batteryName}, address=0x{address:X12}, platform={DeviceInfo.Platform}, app={AppInfo.VersionString}, client={typeof(WattCycle.Core.WattCycleBtClient).Assembly.FullName}");
	}

	public void Write(string message)
	{
		lock (_gate)
		{
			var entry = $"{DateTimeOffset.Now:HH:mm:ss.fff} +{_elapsed.Elapsed.TotalSeconds:F3}s {message}";
			Debug.WriteLine(entry);
			if (FilePath is null) return;
			try
			{
				File.AppendAllText(FilePath, entry + Environment.NewLine);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Control log write failed: {ex}");
			}
		}
	}
}
