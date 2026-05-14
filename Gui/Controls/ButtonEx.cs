namespace Gui.Controls;

public class ButtonEx : Button
{
	private static readonly Color _bgColor = Colors.Black; // BtHack.App.Current?.RequestedTheme == AppTheme.Dark ? Colors.Black : Colors.White;
	private static readonly Color _fgColor = Colors.White; // BtHack.App.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

	public ButtonEx()
	{
		BackgroundColor = _bgColor;
		TextColor = _fgColor;
		BorderColor = _bgColor;
		FontFamily = FluentIcons.IconFontFamily;
#if WINDOWS || MACCATALYST
		FontSize = 24;
#elif ANDROID || IOS
		FontSize = 18;
#endif
	}

	public static readonly BindableProperty IconProperty =
		BindableProperty.Create(nameof(Icon), typeof(String), typeof(ButtonEx), string.Empty, propertyChanged: OnIconChanged);

	public static readonly BindableProperty BorderProperty =
		BindableProperty.Create(nameof(Border), typeof(bool), typeof(ButtonEx), false, propertyChanged: OnBorderChanged);

	public static readonly BindableProperty IsLoadingProperty =
		BindableProperty.Create(nameof(IsLoading), typeof(bool), typeof(ButtonEx), false, propertyChanged: OnIsLoadingChanged);

	public String Icon
	{
		get => (String)GetValue(IconProperty);
		set => SetValue(IconProperty, value);
	}

	private static void OnIconChanged(BindableObject bindable, object oldValue, object newValue)
	{
		var button = (ButtonEx)bindable;
		String icon = (String)newValue;
		if (!String.IsNullOrEmpty(icon))
			button.Text = icon;
	}

	public bool Border
	{
		get => (bool)GetValue(BorderProperty);
		set => SetValue(BorderProperty, value);
	}

	private static void OnBorderChanged(BindableObject bindable, object oldValue, object newValue)
	{
		var button = (ButtonEx)bindable;
		bool border = (bool)newValue;
		if (border)
			button.BorderColor = _fgColor;
		else
			button.BorderColor = _bgColor;
	}

	public bool IsLoading
	{
		get => (bool)GetValue(IsLoadingProperty);
		set => SetValue(IsLoadingProperty, value);
	}
	private static void OnIsLoadingChanged(BindableObject bindable, object oldValue, object newValue)
	{
		var button = (ButtonEx)bindable;
		bool isLoading = (bool)newValue;
		if (isLoading)
		{
			//button.Text = "Loading...";
			button.IsEnabled = false;
		}
		else
		{
			//button.Text = "Submit";
			button.IsEnabled = true;
		}
	}


}

