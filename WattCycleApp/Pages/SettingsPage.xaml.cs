using System.Collections.ObjectModel;

using WattCycleApp.Services;
using static WattCycleApp.Controls.ToolBar;

namespace WattCycleApp.Pages;

public partial class SettingsPage : ContentPage
{
	public ObservableCollection<SettingRow> Settings { get; } =
	[
		new(
			"Batteries",
			"Number of batteries to scan",
			BatteryMonitorSettings.BatteryCountPreferenceKey,
			BatteryMonitorSettings.GetText(BatteryMonitorSettings.BatteryCountPreferenceKey, 4)),
		new(
			"Scan",
			"Discovery timeout in minutes",
			BatteryMonitorSettings.ScanMinutesPreferenceKey,
			BatteryMonitorSettings.GetText(BatteryMonitorSettings.ScanMinutesPreferenceKey, 2)),
		new(
			"Loop",
			"Delay between monitor cycles in minutes",
			BatteryMonitorSettings.LoopMinutesPreferenceKey,
			BatteryMonitorSettings.GetText(BatteryMonitorSettings.LoopMinutesPreferenceKey, 5)),
		new(
			"Timeout",
			"Battery data timeout in seconds",
			BatteryMonitorSettings.TimeoutSecondsPreferenceKey,
			BatteryMonitorSettings.GetText(BatteryMonitorSettings.TimeoutSecondsPreferenceKey, 30))
	];

	public SettingsPage()
	{
		InitializeComponent();
		BindingContext = this;

		horToolBar.Create(ePages.Settings, StackOrientation.Horizontal);
		verToolBar.Create(ePages.Settings, StackOrientation.Vertical);
	}

	protected override void OnSizeAllocated(double width, double height)
	{
		if ((width == -1) || (height == -1))
		{
			return;
		}

#if ANDROID || IOS
		if (width > height)
		{
			horToolBar.IsVisible = false;
			verToolBar.IsVisible = true;
		}
		else
		{
			horToolBar.IsVisible = true;
			verToolBar.IsVisible = false;
		}
#else
		horToolBar.IsVisible = false;
		verToolBar.IsVisible = true;
#endif

		base.OnSizeAllocated(width, height);
	}

	private void OnSettingTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is Entry { BindingContext: SettingRow setting })
		{
			setting.Text = e.NewTextValue ?? string.Empty;
			BatteryMonitorSettings.SetValue(setting.PreferenceKey, setting.Text);
		}
	}

	public sealed class SettingRow(string name, string description, string preferenceKey, string text)
	{
		public string Name { get; } = name;
		public string Description { get; } = description;
		public string PreferenceKey { get; } = preferenceKey;
		public string Text { get; set; } = text;
	}
}
