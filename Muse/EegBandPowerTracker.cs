using System.Numerics;

namespace Muse.Core;

public sealed class EegBandPowerTracker
{
    private readonly double[] _buffer;
    private readonly double[] _analysisBuffer;
    private readonly int _sampleRate;
    private readonly BiquadNotchFilter? _notchFilter;
    private readonly object _gate = new();
    private int _nextIndex;
    private int _count;
    private int _version;
    private int _lastCalculatedVersion;
    private int _samplesSinceLastCalculation;
    private long _calculationCount;
    private MuseBandPowers _lastBands;

    public EegBandPowerTracker(string name, int windowSamples, int sampleRate, int hopSamples)
    {
        Name = name;
        _buffer = new double[windowSamples];
        _analysisBuffer = new double[windowSamples];
        _sampleRate = sampleRate;
        HopSamples = hopSamples;
        _notchFilter = MuseBluetoothConstants.EnableBandPowerNotch
            ? BiquadNotchFilter.TryCreate(
                sampleRate,
                MuseBluetoothConstants.BandPowerNotchFrequencyHz,
                MuseBluetoothConstants.BandPowerNotchQ)
            : null;
    }

    public string Name { get; }

    public int HopSamples { get; }

    public void AddSamples(IReadOnlyList<double> samples)
    {
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                var index = _nextIndex;
                _buffer[index] = sample;
                _analysisBuffer[index] = _notchFilter?.Process(sample) ?? sample;
                _nextIndex = (_nextIndex + 1) % _buffer.Length;
                _count = Math.Min(_count + 1, _buffer.Length);
            }

            _version++;
            _samplesSinceLastCalculation += samples.Count;
        }
    }

    public bool TryCalculate(out MuseBandPowers bands) => TryCalculate(out bands, out _);

    public bool TryCalculate(out MuseBandPowers bands, out MuseBandPowerDiagnostic? diagnostic)
    {
        double[] snapshot;
        double[] analysisSnapshot;
        lock (_gate)
        {
            bands = _lastBands;
            diagnostic = null;
            if (_count < _buffer.Length || _version == _lastCalculatedVersion || _samplesSinceLastCalculation < HopSamples)
            {
                return false;
            }

            snapshot = new double[_buffer.Length];
            analysisSnapshot = new double[_analysisBuffer.Length];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var index = (_nextIndex + i) % _buffer.Length;
                snapshot[i] = _buffer[index];
                analysisSnapshot[i] = _analysisBuffer[index];
            }

            _lastCalculatedVersion = _version;
            _samplesSinceLastCalculation = Math.Max(0, _samplesSinceLastCalculation - HopSamples);
        }

        var psd = CalculatePowerSpectralDensity(analysisSnapshot, _sampleRate);
        var binWidth = (double)_sampleRate / snapshot.Length;
        var delta = CalculateAbsoluteBandPower(psd, binWidth, snapshot.Length, _sampleRate, 1, 4);
        var theta = CalculateAbsoluteBandPower(psd, binWidth, snapshot.Length, _sampleRate, 4, 8);
        var alpha = CalculateAbsoluteBandPower(psd, binWidth, snapshot.Length, _sampleRate, 7.5, 13);
        var beta = CalculateAbsoluteBandPower(psd, binWidth, snapshot.Length, _sampleRate, 13, 30);
        var gamma = CalculateAbsoluteBandPower(
            psd,
            binWidth,
            snapshot.Length,
            _sampleRate,
            30,
            44,
            true,
            MuseBluetoothConstants.GammaSpikeLimitMedianMultiplier);
        var gammaPeak = FindPeak(psd, binWidth, snapshot.Length, _sampleRate, 30, 44);
        var highPeak = FindPeak(psd, binWidth, snapshot.Length, _sampleRate, 30, 80);
        var line50Power = GetNearestBinPower(psd, snapshot.Length, _sampleRate, 50);
        var line60Power = GetNearestBinPower(psd, snapshot.Length, _sampleRate, 60);

        bands = new MuseBandPowers(
            delta.LogPower,
            theta.LogPower,
            alpha.LogPower,
            beta.LogPower,
            gamma.LogPower);

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0.0;
        var absSum = 0.0;
        var squareSum = 0.0;
        foreach (var sample in snapshot)
        {
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
            sum += sample;
            absSum += Math.Abs(sample);
            squareSum += sample * sample;
        }

        long calculationCount;
        lock (_gate)
        {
            _lastBands = bands;
            calculationCount = ++_calculationCount;
        }

        diagnostic = new MuseBandPowerDiagnostic(
            Name,
            calculationCount,
            snapshot.Length,
            HopSamples,
            _sampleRate,
            _notchFilter?.FrequencyHz ?? 0.0,
            _notchFilter?.Q ?? 0.0,
            min,
            max,
            sum / snapshot.Length,
            absSum / snapshot.Length,
            Math.Sqrt(squareSum / snapshot.Length),
            gamma.IntegratedPower,
            gamma.LogPower,
            gamma.LogPower * 10.0,
            gammaPeak.FrequencyHz,
            gammaPeak.Power,
            highPeak.FrequencyHz,
            highPeak.Power,
            line50Power,
            line60Power,
            gamma.SpikeLimitMultiplier,
            gamma.SpikeLimit);

        return true;
    }

    private static BandPowerResult CalculateAbsoluteBandPower(
        double[] psd,
        double binWidth,
        int sampleCount,
        int sampleRate,
        double lowHz,
        double highHz,
        bool limitNarrowSpikes = false,
        double spikeLimitMedianMultiplier = 3.0)
    {
        var firstBin = Math.Max(1, (int)Math.Ceiling(lowHz * sampleCount / sampleRate));
        var lastBin = Math.Min((sampleCount / 2) - 1, (int)Math.Floor(highHz * sampleCount / sampleRate));
        var powerSum = 0.0;
        var spikeLimit = limitNarrowSpikes ? CalculateSpikeLimit(psd, firstBin, lastBin, spikeLimitMedianMultiplier) : double.PositiveInfinity;

        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            powerSum += Math.Min(psd[bin], spikeLimit) * binWidth;
        }

        var logPower = Math.Log10(Math.Max(powerSum, 1e-12));
        return new BandPowerResult(powerSum, logPower, limitNarrowSpikes ? spikeLimitMedianMultiplier : 0.0, spikeLimit);
    }

    private static double CalculateSpikeLimit(double[] psd, int firstBin, int lastBin, double medianMultiplier)
    {
        if (lastBin < firstBin)
        {
            return double.PositiveInfinity;
        }

        var bandBins = new double[lastBin - firstBin + 1];
        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            bandBins[bin - firstBin] = psd[bin];
        }

        Array.Sort(bandBins);
        var median = bandBins[bandBins.Length / 2];
        return Math.Max(median * medianMultiplier, double.Epsilon);
    }

    private static FrequencyPower FindPeak(double[] psd, double binWidth, int sampleCount, int sampleRate, double lowHz, double highHz)
    {
        var firstBin = Math.Max(1, (int)Math.Ceiling(lowHz * sampleCount / sampleRate));
        var lastBin = Math.Min((sampleCount / 2) - 1, (int)Math.Floor(highHz * sampleCount / sampleRate));
        var peakBin = firstBin;
        var peakPower = 0.0;

        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            if (psd[bin] > peakPower)
            {
                peakBin = bin;
                peakPower = psd[bin];
            }
        }

        return new FrequencyPower(peakBin * binWidth, peakPower);
    }

    private static double GetNearestBinPower(double[] psd, int sampleCount, int sampleRate, double hz)
    {
        var bin = (int)Math.Round(hz * sampleCount / sampleRate);
        return bin >= 0 && bin < psd.Length ? psd[bin] : 0.0;
    }

    private static double[] CalculatePowerSpectralDensity(double[] samples, int sampleRate)
    {
        return IsPowerOfTwo(samples.Length)
            ? CalculatePowerSpectralDensityFft(samples, sampleRate)
            : CalculatePowerSpectralDensityDft(samples, sampleRate);
    }

    private static double[] CalculatePowerSpectralDensityFft(double[] samples, int sampleRate)
    {
        var n = samples.Length;
        var mean = samples.Average();
        var psd = new double[(n / 2) + 1];
        var windowPower = 0.0;
        var spectrum = new Complex[n];

        for (var i = 0; i < n; i++)
        {
            var window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            spectrum[i] = new Complex((samples[i] - mean) * window, 0.0);
            windowPower += window * window;
        }

        TransformForward(spectrum);

        for (var bin = 0; bin < psd.Length; bin++)
        {
            var oneSidedScale = (bin == 0 || bin == n / 2) ? 1.0 : 2.0;
            var real = spectrum[bin].Real;
            var imaginary = spectrum[bin].Imaginary;
            psd[bin] = oneSidedScale * (real * real + imaginary * imaginary) / Math.Max(sampleRate * windowPower, double.Epsilon);
        }

        return psd;
    }

    private static double[] CalculatePowerSpectralDensityDft(double[] samples, int sampleRate)
    {
        var n = samples.Length;
        var mean = samples.Average();
        var psd = new double[(n / 2) + 1];
        var windowPower = 0.0;
        var windowedSamples = new double[n];

        for (var i = 0; i < n; i++)
        {
            var window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            windowedSamples[i] = (samples[i] - mean) * window;
            windowPower += window * window;
        }

        for (var bin = 0; bin < psd.Length; bin++)
        {
            var real = 0.0;
            var imaginary = 0.0;
            for (var i = 0; i < n; i++)
            {
                var angle = 2.0 * Math.PI * bin * i / n;
                real += windowedSamples[i] * Math.Cos(angle);
                imaginary -= windowedSamples[i] * Math.Sin(angle);
            }

            var oneSidedScale = (bin == 0 || bin == n / 2) ? 1.0 : 2.0;
            psd[bin] = oneSidedScale * (real * real + imaginary * imaginary) / Math.Max(sampleRate * windowPower, double.Epsilon);
        }

        return psd;
    }

    private static void TransformForward(Complex[] values)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var wLength = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                var halfLength = length >> 1;
                for (var j = 0; j < halfLength; j++)
                {
                    var even = values[i + j];
                    var odd = values[i + j + halfLength] * w;
                    values[i + j] = even + odd;
                    values[i + j + halfLength] = even - odd;
                    w *= wLength;
                }
            }
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private sealed class BiquadNotchFilter
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        private BiquadNotchFilter(double frequencyHz, double q, double b0, double b1, double b2, double a1, double a2)
        {
            FrequencyHz = frequencyHz;
            Q = q;
            _b0 = b0;
            _b1 = b1;
            _b2 = b2;
            _a1 = a1;
            _a2 = a2;
        }

        public double FrequencyHz { get; }

        public double Q { get; }

        public static BiquadNotchFilter? TryCreate(int sampleRate, double frequencyHz, double q)
        {
            if (sampleRate <= 0 || frequencyHz <= 0.0 || frequencyHz >= sampleRate / 2.0 || q <= 0.0)
            {
                return null;
            }

            var omega = 2.0 * Math.PI * frequencyHz / sampleRate;
            var cosine = Math.Cos(omega);
            var alpha = Math.Sin(omega) / (2.0 * q);

            var b0 = 1.0;
            var b1 = -2.0 * cosine;
            var b2 = 1.0;
            var a0 = 1.0 + alpha;
            var a1 = -2.0 * cosine;
            var a2 = 1.0 - alpha;

            return new BiquadNotchFilter(
                frequencyHz,
                q,
                b0 / a0,
                b1 / a0,
                b2 / a0,
                a1 / a0,
                a2 / a0);
        }

        public double Process(double sample)
        {
            var output = (_b0 * sample) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
            _x2 = _x1;
            _x1 = sample;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }

    private readonly record struct BandPowerResult(double IntegratedPower, double LogPower, double SpikeLimitMultiplier, double SpikeLimit);

    private readonly record struct FrequencyPower(double FrequencyHz, double Power);
}
