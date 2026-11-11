#if WINDOWS || ANDROID || IOS || MACCATALYST || MUSE_PROTOCOL_TESTS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Muse.Core;

// Protocol, stream lifecycle and signal processing are shared by every BLE backend.
public sealed partial class MuseBtClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, long> _notificationCounts = new();
    private readonly ConcurrentDictionary<Guid, EegBandPowerTracker> _eegBandTrackers = new();
    private readonly ConcurrentDictionary<Guid, MuseEegPacketStats> _eegPacketStats = new();

    public event EventHandler<string>? DiagnosticMessage;

    public event EventHandler<string>? InfoMessage;

    public event EventHandler<string>? ConnectionStatusChanged;

    public event EventHandler<MuseNotification>? NotificationReceived;

    public event EventHandler<MuseEegPacketDiagnostic>? EegPacketDiagnostic;

    public event EventHandler<MuseBandPowerDiagnostic>? BandPowerDiagnostic;

    public event EventHandler<MuseBandPowerReading>? BandPowersCalculated;

    public async Task ConnectAndStreamAsync(MuseDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
    {
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (reconnectAttempt > 0)
                {
                    OnDiagnostic($"Reconnect attempt {reconnectAttempt} to {discovered.DisplayName}...");
                }

                // Samples from a previous session must not enter a new FFT window.
                _notificationCounts.Clear();
                _eegBandTrackers.Clear();
                _eegPacketStats.Clear();
                await ConnectAndStreamOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
            {
                OnInfo($"Muse BT stream failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync(sendStopCommand: false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupConnectionAsync(sendStopCommand: true);
    }

    private long TotalNotifications() => _notificationCounts.Values.Sum();

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            MuseBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            MuseBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task StartClassicMuseStreamAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Sending Muse classic start sequence: h, s, p21, d.");
        await SendControlCommandAsync("h", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("s", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("p21", cancellationToken);
        await Task.Delay(200, cancellationToken);
        await SendControlCommandAsync("d", cancellationToken);
    }

    private void PublishBandSummaries()
    {
        foreach (var tracker in _eegBandTrackers.Values.OrderBy(tracker => tracker.Name))
        {
            if (tracker.TryCalculate(out var bands, out var diagnostic))
            {
                if (diagnostic is not null)
                {
                    BandPowerDiagnostic?.Invoke(this, diagnostic);
                    OnDiagnostic(FormatBandPowerDiagnostic(diagnostic));
                }

                BandPowersCalculated?.Invoke(this, new MuseBandPowerReading(tracker.Name, bands));
            }
        }
    }

    private static string ShortUuid(Guid uuid) => uuid.ToString()[4..8];

    private static string FormatBandPowerDiagnostic(MuseBandPowerDiagnostic diagnostic) =>
        $"PSD {ShortSensorName(diagnostic.SensorName),-4} #{diagnostic.Count,5} " +
        $"n={diagnostic.WindowSamples} hop={diagnostic.HopSamples} fs={diagnostic.SampleRate} notch={FormatNotch(diagnostic)} " +
        $"uV[min={diagnostic.MinMicrovolts,7:F1}, max={diagnostic.MaxMicrovolts,7:F1}, mean={diagnostic.MeanMicrovolts,7:F2}, " +
        $"meanAbs={diagnostic.MeanAbsMicrovolts,6:F1}, rms={diagnostic.RmsMicrovolts,6:F1}] " +
        $"gamma[pow={diagnostic.GammaIntegratedPower:E3}, final={diagnostic.GammaLogPower,6:F3}, dB={diagnostic.GammaDb,6:F2}, " +
        $"peak={diagnostic.GammaPeakHz,5:F1}Hz/{diagnostic.GammaPeakPower:E3}, spike={diagnostic.GammaSpikeLimit:E3}/x{diagnostic.GammaSpikeLimitMultiplier:F1}] " +
        $"highPeak={diagnostic.HighPeakHz,5:F1}Hz/{diagnostic.HighPeakPower:E3} line50={diagnostic.Line50HzPower:E3} line60={diagnostic.Line60HzPower:E3}";

    private static string FormatNotch(MuseBandPowerDiagnostic diagnostic) =>
        diagnostic.NotchFrequencyHz > 0.0 ? $"{diagnostic.NotchFrequencyHz:F1}Hz/q{diagnostic.NotchQ:F1}" : "off";

    private static string ShortSensorName(string name) => name.StartsWith("EEG ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);

    private static byte[] EncodeControlCommand(string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        if (bytes.Length > byte.MaxValue)
            throw new InvalidOperationException($"Control command too long: {command}");
        var payload = new byte[bytes.Length + 1];
        payload[0] = (byte)bytes.Length;
        bytes.CopyTo(payload, 1);
        return payload;
    }

    private void HandleNotification(Guid uuid, string name, MuseSensorKind kind, byte[] bytes)
    {
        var count = _notificationCounts.AddOrUpdate(uuid, 1, (_, value) => value + 1);
        if (kind == MuseSensorKind.Eeg && MusePacketDecoder.TryDecodeEeg(bytes, out var packet))
        {
            var tracker = _eegBandTrackers.GetOrAdd(uuid, _ => new EegBandPowerTracker(
                name, MuseBluetoothConstants.DefaultBandWindowSamples,
                MuseBluetoothConstants.EegSampleRate, MuseBluetoothConstants.DefaultBandHopSamples));
            tracker.AddSamples(packet.Samples);
            var stats = _eegPacketStats.GetOrAdd(uuid, _ => new MuseEegPacketStats());
            EegPacketDiagnostic?.Invoke(this, stats.Track(name, count, packet));
        }
        NotificationReceived?.Invoke(this, new MuseNotification(name, uuid, kind, bytes, count));
    }

    private async Task RunStreamingLoopAsync(CancellationToken cancellationToken)
    {
        OnDiagnostic("Streaming.");
        try
        {
            var keepAliveTimer = Stopwatch.StartNew();
            var lastNotificationTotal = TotalNotifications();
            var lastNotificationProgress = DateTimeOffset.UtcNow;
            var disconnected = DisconnectedTask;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var tick = Task.Delay(MuseBluetoothConstants.BandPowerPublishInterval, tickCts.Token);
                if (await Task.WhenAny(tick, disconnected) == disconnected)
                {
                    tickCts.Cancel();
                    OnDiagnostic("Connection lost; leaving stream loop for reconnect.");
                    break;
                }
                await tick;
                PublishBandSummaries();
                var total = TotalNotifications();
                if (total > lastNotificationTotal)
                {
                    lastNotificationTotal = total;
                    lastNotificationProgress = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - lastNotificationProgress >= MuseBluetoothConstants.NotificationStallReconnectTimeout)
                {
                    OnInfo($"No Muse notifications for {MuseBluetoothConstants.NotificationStallReconnectTimeout.TotalSeconds:F0}s; reconnecting.");
                    break;
                }
                if (keepAliveTimer.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    keepAliveTimer.Restart();
                    OnDiagnostic($"Heartbeat: notifications=[{string.Join(", ", _notificationCounts.Select(p => $"{ShortUuid(p.Key)}={p.Value}"))}]");
                    await SendControlCommandAsync("k", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (IsConnected)
            {
                // A failed stop must not prevent disposal/reconnection, or delay Stop indefinitely.
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await SendControlCommandAsync("h", stopCts.Token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException)
                {
                    OnDiagnostic($"Stop command failed: {ex.Message}");
                }
            }
        }
    }
}
#endif
