using System.Collections.ObjectModel;

namespace Gui.Controls;

public partial class LogView : ContentView
{
	private const int MaxEntries = 500;
	private readonly object _logFileGate = new();
	private string? _logFilePath;

	public static readonly BindableProperty EntriesProperty = BindableProperty.Create(
		nameof(Entries),
		typeof(ObservableCollection<string>),
		typeof(LogView),
		defaultValueCreator: _ => new ObservableCollection<string>());

	public static readonly BindableProperty StatusTextProperty = BindableProperty.Create(
		nameof(StatusText),
		typeof(string),
		typeof(LogView),
		"Idle");

	public static readonly BindableProperty IsStartEnabledProperty = BindableProperty.Create(
		nameof(IsStartEnabled),
		typeof(bool),
		typeof(LogView),
		true);

	public static readonly BindableProperty IsStopEnabledProperty = BindableProperty.Create(
		nameof(IsStopEnabled),
		typeof(bool),
		typeof(LogView),
		false);

	public event EventHandler? StartClicked;

	public event EventHandler? StopClicked;

	public LogView()
	{
		InitializeComponent();
	}

	public string? LogFilePath => _logFilePath;

	public ObservableCollection<string> Entries
	{
		get => (ObservableCollection<string>)GetValue(EntriesProperty);
		set => SetValue(EntriesProperty, value);
	}

	public string StatusText
	{
		get => (string)GetValue(StatusTextProperty);
		set => SetValue(StatusTextProperty, value);
	}

	public bool IsStartEnabled
	{
		get => (bool)GetValue(IsStartEnabledProperty);
		set => SetValue(IsStartEnabledProperty, value);
	}

	public bool IsStopEnabled
	{
		get => (bool)GetValue(IsStopEnabledProperty);
		set => SetValue(IsStopEnabledProperty, value);
	}

	public string? StartFileLog(string prefix)
	{
		var appDataDirectory = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
		var path = Path.Combine(appDataDirectory, $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

		lock (_logFileGate)
		{
			try
			{
				Directory.CreateDirectory(appDataDirectory);
				File.WriteAllText(path, string.Empty);
				_logFilePath = path;
			}
			catch
			{
				_logFilePath = null;
			}

			return _logFilePath;
		}
	}

	public void StopFileLog()
	{
		lock (_logFileGate)
		{
			_logFilePath = null;
		}
	}

	public void Add(string message)
	{
		var entry = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}";
		AppendToFile(entry);
		MainThread.BeginInvokeOnMainThread(() =>
		{
			Entries.Insert(0, entry); // .Add
									  //while (Entries.Count > MaxEntries)
									  //{
									  //    Entries.RemoveAt(0);
									  //}

			//LogEntriesView.ScrollTo(Entries.Count - 1, position: ScrollToPosition.End, animate: false);
		});
	}

	private void AppendToFile(string entry)
	{
		lock (_logFileGate)
		{
			if (_logFilePath is null)
			{
				return;
			}

			try
			{
				File.AppendAllText(_logFilePath, entry + Environment.NewLine);
			}
			catch
			{
				_logFilePath = null;
			}
		}
	}

	public void Clear()
	{
		MainThread.BeginInvokeOnMainThread(Entries.Clear);
	}

	private void OnClearClicked(object? sender, EventArgs e)
	{
		Clear();
	}

	private void OnStartClicked(object? sender, EventArgs e)
	{
		StartClicked?.Invoke(this, EventArgs.Empty);
	}

	private void OnStopClicked(object? sender, EventArgs e)
	{
		StopClicked?.Invoke(this, EventArgs.Empty);
	}
}
