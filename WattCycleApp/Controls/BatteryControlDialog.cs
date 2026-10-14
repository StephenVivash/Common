using CommunityToolkit.Maui.Views;

namespace WattCycleApp.Controls;

internal sealed class BatteryControlDialog : Popup<string>
{
	private bool _closing;

	public BatteryControlDialog(string batteryName)
	{
		this.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#202020"));
		var buttons = new Grid
		{
			ColumnDefinitions = new ColumnDefinitionCollection
			{
				new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)
			},
			ColumnSpacing = 8
		};
		var actions = new[] { "On", "Off", "Cancel" };
		for (var column = 0; column < actions.Length; column++)
		{
			var action = actions[column];
			var button = new Button { Text = action };
			button.Clicked += async (_, _) =>
			{
				if (_closing) return;
				_closing = true;
				buttons.IsEnabled = false;
				await CloseAsync(action);
			};
			buttons.Add(button, column, 0);
		}

		Content = new VerticalStackLayout
		{
			WidthRequest = 270,
			Spacing = 14,
			Children =
			{
				new Label { Text = batteryName, FontSize = 18, FontAttributes = FontAttributes.Bold },
				new Label { Text = "Turn this battery on or off." },
				buttons
			}
		};
	}
}
