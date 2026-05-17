using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Maui;
using LiveChartsCore.SkiaSharpView.Painting;

using SkiaSharp;

using WattCycleApp.Models;
using WattCycleApp.Services;

namespace WattCycleApp.Pages;

public partial class GraphPage : ContentPage
{
	private static readonly SKColor[] BatteryColors =
	[
		SKColors.Red,
		SKColors.Cyan,
		SKColors.Orange,
		SKColors.LimeGreen,
		SKColors.DeepSkyBlue,
		SKColors.Magenta,
		SKColors.Yellow,
		SKColors.White
	];

	private readonly BatteryHistoryStore _historyStore = BatteryHistoryStore.Default;
	private CartesianChart? _historyChart;
	private bool _refreshQueued;

	public Axis[] XAxes { get; } =
	[
		new Axis
		{
			Labeler = FormatTimestamp,
			//LabelsRotation = 15,
			SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80))
		}
	];

	public Axis[] YAxes { get; } =
	[
		new Axis
		{
			//Name = "SOC % / |W|",
			MinLimit = 0,
			SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80))
		}
	];

	public GraphPage()
	{
		InitializeComponent();
		BindingContext = this;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_historyStore.SampleAdded += OnHistorySampleAdded;
		await RefreshAsync();
	}

	protected override void OnDisappearing()
	{
		_historyStore.SampleAdded -= OnHistorySampleAdded;
		base.OnDisappearing();
	}

	private void OnHistorySampleAdded(object? sender, BatteryHistorySample sample)
	{
		_ = MainThread.InvokeOnMainThreadAsync(async () =>
		{
			if (_refreshQueued)
			{
				return;
			}

			_refreshQueued = true;
			await Task.Delay(250);
			_refreshQueued = false;
			await RefreshAsync();
		});
	}

	private async Task RefreshAsync()
	{
		var samples = await _historyStore.GetSamplesAsync();
		var series = BuildSeries(samples);
		var hasSeries = series.Count > 0;
		await EnsureChartAsync(series);
		EmptyStateLabel.IsVisible = !hasSeries;
	}

	private async Task EnsureChartAsync(IReadOnlyList<ISeries> series)
	{
		if (series.Count == 0)
		{
			ChartHost.Content = null;
			_historyChart = null;
			return;
		}

		if (_historyChart is not null)
		{
			_historyChart.Series = series;
			return;
		}

		_historyChart = new CartesianChart
		{
			Series = Array.Empty<ISeries>(),
			XAxes = XAxes,
			YAxes = YAxes,
			LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
			ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.X
		};
		ChartHost.Content = _historyChart;
		await Task.Yield();
		_historyChart.Series = series;
	}

	private static IReadOnlyList<ISeries> BuildSeries(IReadOnlyList<BatteryHistorySample> samples)
	{
		return samples
			.GroupBy(GetBatteryKey)
			.OrderBy(group => group.Min(sample => sample.BatteryName), StringComparer.OrdinalIgnoreCase)
			.ThenBy(group => group.Key)
			.SelectMany((group, index) =>
			{
				var orderedSamples = group
					.OrderBy(sample => sample.Timestamp)
					.ToArray();

				var color = BatteryColors[index % BatteryColors.Length];
				var name = orderedSamples.First().BatteryName;
				var socPoints = orderedSamples
					.Select(sample => new ObservablePoint(sample.Timestamp.ToUnixTimeSeconds(), sample.StateOfChargePercent))
					.ToArray();
				var wattPoints = orderedSamples
					.Select(sample => new ObservablePoint(sample.Timestamp.ToUnixTimeSeconds(), Math.Abs(sample.PowerWatts)))
					.ToArray();

				return new ISeries[]
				{
					CreateLineSeries($"{name} SOC", socPoints, color, 4, 0), // 5 4
					CreateLineSeries($"{name} |W|", wattPoints, color, 2, 0)
				};
			})
			.ToArray();
	}

	private static LineSeries<ObservablePoint> CreateLineSeries(
		string name,
		IReadOnlyList<ObservablePoint> values,
		SKColor color,
		float strokeWidth,
		double geometrySize) =>
		new()
		{
			Name = name,
			Values = values,
			IsVisibleAtLegend = false,
			Fill = null,
			GeometrySize = geometrySize,
			LineSmoothness = 1,
			Stroke = new SolidColorPaint(color, strokeWidth),
			GeometryStroke = new SolidColorPaint(color, strokeWidth),
			GeometryFill = new SolidColorPaint(color)
		};

	private static string GetBatteryKey(BatteryHistorySample sample)
	{
		if (sample.BluetoothAddress != 0)
		{
			return sample.BluetoothAddress.ToString("X");
		}

		return sample.BatteryName;
	}

	private static string FormatTimestamp(double value)
	{
		const double MinUnixSeconds = -62135596800;
		const double MaxUnixSeconds = 253402300799;
		if (!double.IsFinite(value) || value < MinUnixSeconds || value > MaxUnixSeconds)
		{
			return string.Empty;
		}

		return DateTimeOffset.FromUnixTimeSeconds((long)value).LocalDateTime.ToString("HH:mm");
	}

}
