namespace WattCycleApp.Services;

public static class BatteryMonitorSettings
{
	public const string BatteryCountPreferenceKey = "battery-count";
	public const string ScanMinutesPreferenceKey = "scan-minutes";
	public const string LoopMinutesPreferenceKey = "loop-minutes";
	public const string TimeoutSecondsPreferenceKey = "timeout-seconds";

	public static int BatteryLimit => ReadInt(BatteryCountPreferenceKey, 4, 1, 16);
	public static double ScanMinutes => ReadDouble(ScanMinutesPreferenceKey, 2, 0.1, 60);
	public static double LoopMinutes => ReadDouble(LoopMinutesPreferenceKey, 5, 0.1, 1440);
	public static double TimeoutSeconds => ReadDouble(TimeoutSecondsPreferenceKey, 30, 1, 600);

	public static void SetValue(string key, string text)
	{
		if (key == BatteryCountPreferenceKey)
		{
			if (!int.TryParse(text, out var value))
			{
				value = 4;
			}

			Preferences.Set(key, Math.Clamp(value, 1, 16));
			return;
		}

		Preferences.Set(key, text);
	}

	public static string GetText(string key, double defaultValue)
	{
		if (key == BatteryCountPreferenceKey)
		{
			return Preferences.Get(key, (int)defaultValue).ToString();
		}

		return GetStringValue(key, defaultValue.ToString("0.###"));
	}

	private static int ReadInt(string key, int defaultValue, int min, int max)
	{
		int value;
		if (key == BatteryCountPreferenceKey)
		{
			value = Preferences.Get(key, defaultValue);
		}
		else if (!int.TryParse(GetStringValue(key, defaultValue.ToString()), out value))
		{
			value = defaultValue;
		}

		return Math.Clamp(value, min, max);
	}

	private static double ReadDouble(string key, double defaultValue, double min, double max)
	{
		var text = GetStringValue(key, defaultValue.ToString("0.###"));
		if (!double.TryParse(text, out var value))
		{
			value = defaultValue;
		}

		return Math.Clamp(value, min, max);
	}

	private static string GetStringValue(string key, string defaultValue)
	{
		try
		{
			return Preferences.Get(key, defaultValue);
		}
		catch (InvalidCastException)
		{
			Preferences.Remove(key);
			Preferences.Set(key, defaultValue);
			return defaultValue;
		}
	}
}
