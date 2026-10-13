using System.Diagnostics;

namespace WattCycleApp.Services;

internal sealed class BatteryPollingLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N")[..8];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public BatteryPollingLog()
    {
        Write($"Monitoring session started: platform={DeviceInfo.Platform}, app={AppInfo.VersionString}, " +
            $"client build={typeof(WattCycle.Core.WattCycleBtClient).Module.ModuleVersionId}");
    }

    public void Write(string message)
    {
        var entry = $"{DateTimeOffset.Now:O} [{_session}] +{_elapsed.Elapsed.TotalSeconds:F3}s {message}";
        Debug.WriteLine(entry);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(FileSystem.AppDataDirectory);
                var path = Path.Combine(FileSystem.AppDataDirectory, "WattCyclePolling.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                    File.Move(path, Path.Combine(FileSystem.AppDataDirectory, "WattCyclePolling.previous.log"), overwrite: true);
                File.AppendAllText(path, entry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Logging must never interrupt monitoring or Bluetooth cleanup.
                Debug.WriteLine($"Polling log write failed: {ex}");
            }
        }
    }
}
