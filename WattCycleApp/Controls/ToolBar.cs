
using CommunityToolkit.Maui.Core;

using Gui.Services;
using Gui.Controls;

namespace WattCycleApp.Controls;

public partial class ToolBar : ContentView
{

	public enum ePages { None, Battery, Graph, Settings };

	private static Color _bgColor = App.Current?.RequestedTheme == AppTheme.Dark ? Colors.Black : Colors.White;
	private ePages _page = ePages.None;
	private StackLayout _slMenu;

	private ButtonEx _btnBattery;
	private ButtonEx _btnGraph;
	private ButtonEx _btnSettings;


#if WINDOWS || MACCATALYST
	public const DockPosition VerticalDockPosition = DockPosition.Left;
#elif ANDROID || IOS
	public const DockPosition VerticalDockPosition = DockPosition.Right;
#endif

	public ToolBar()
	{
		BindingContext = this;
		BackgroundColor = _bgColor;
	}

	protected override void OnSizeAllocated(double width, double height)
	{
		if ((width == -1) || (height == -1))
			return;

		double w = width - 10;

		w = width - 40;

		w /= 2;
		w -= 10;

		w /= 2;

		base.OnSizeAllocated(width, height);
	}

	public void Create(ePages page, StackOrientation orientation)
	{
		var o = orientation == StackOrientation.Vertical ? "V" : "H";
		var b = new RegisterInViewDirectoryBehavior() { Key = $"{page}{o}Menu" };
		Behaviors.Add(b);

		_page = page;

		Border border = new();

		_slMenu = new StackLayout()
		{
			Orientation = orientation,
			BackgroundColor = _bgColor,
			Margin = new Thickness(0, 0, 0, 0),
			Spacing = 0,
		};
		_btnBattery = new ButtonEx()
		{
			Icon = FluentIcons.Clipboard,

		};
		_btnGraph = new ButtonEx()
		{
			Icon = FluentIcons.Chart,
		};
		_btnSettings = new ButtonEx()
		{
			Icon = FluentIcons.Setting,
		};

		if (orientation == StackOrientation.Vertical)
#if WINDOWS || MACCATALYST
			_slMenu.WidthRequest = 50;
#elif ANDROID || IOS
			_slMenu.WidthRequest = 55;
#endif
		else
			_slMenu.HeightRequest = 45;

		border.Content = _slMenu;

		_slMenu.Children.Add(_btnBattery);
		_slMenu.Children.Add(_btnGraph);
		_slMenu.Children.Add(_btnSettings);

		_btnBattery.Clicked += btnBattery_Clicked;
		_btnGraph.Clicked += btnGraph_Clicked;
		_btnSettings.Clicked += btnSettings_Clicked;

		Content = border;
	}

	private async void btnBattery_Clicked(object sender, EventArgs e)
	{
		if (_page != ePages.Battery)
			await Shell.Current.GoToAsync("//Battery/BatteryRoot", true);
	}

	private async void btnGraph_Clicked(object sender, EventArgs e)
	{
		if (_page != ePages.Graph)
			await Shell.Current.GoToAsync("//Graph/GraphRoot", true);
	}

	private async void btnSettings_Clicked(object sender, EventArgs e)
	{
		if (_page != ePages.Settings)
			await Shell.Current.GoToAsync("//Settings/SettingsRoot", true);
	}

	public static readonly BindableProperty CardColorProperty = BindableProperty.Create(nameof(CardColor),
		typeof(Color), typeof(ToolBar), App.Current.RequestedTheme == AppTheme.Dark ? Colors.Black : Colors.White);

	public Color CardColor
	{
		get => (Color)GetValue(CardColorProperty);
		set => SetValue(CardColorProperty, value);
	}
}
