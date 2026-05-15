
namespace Gui.Services;

#nullable enable

// MFC like find and poke UI objects
public static class ViewDirectory
{
	static readonly object _gate = new();

	// All live instances by type
	static readonly Dictionary<Type, LinkedList<WeakReference<VisualElement>>> _byType = new();

	// Optional key -> instance
	static readonly Dictionary<string, WeakReference<VisualElement>> _byKey = new(StringComparer.Ordinal);

	// Pending actions (run when first instance of T registers)
	static readonly Dictionary<Type, Queue<Action<VisualElement>>> _pendingByType = new();

	// Pending actions for specific key (run when that key registers)
	static readonly Dictionary<string, Queue<Action<VisualElement>>> _pendingByKey = new(StringComparer.Ordinal);

	// --- Get<T>: exact type first, then any assignable (base type support) ---
	public static T? Get<T>() where T : VisualElement
	{
		lock (_gate)
		{
			// exact type
			if (_byType.TryGetValue(typeof(T), out var list))
				for (var n = list.Last; n != null; n = n.Previous)
					if (n.Value.TryGetTarget(out var v) && v is T tv) return tv;

			// any registered type assignable to T (e.g., EegViewPro : EegView)
			foreach (var (type, l) in _byType)
			{
				if (!typeof(T).IsAssignableFrom(type)) continue;
				for (var n = l.Last; n != null; n = n.Previous)
					if (n.Value.TryGetTarget(out var v) && v is T tv) return tv;
			}
		}
		return null;
	}

	// --- GetAll<T>: include assignable types ---
	public static IReadOnlyList<T> GetAll<T>() where T : VisualElement
	{
		var result = new List<T>();
		lock (_gate)
		{
			// exact type block
			if (_byType.TryGetValue(typeof(T), out var list))
				for (var n = list.First; n != null; n = n.Next)
					if (n.Value.TryGetTarget(out var v) && v is T tv) result.Add(tv);

			// plus any assignable types not already included
			foreach (var (type, l) in _byType)
			{
				if (type == typeof(T) || !typeof(T).IsAssignableFrom(type)) continue;
				for (var n = l.First; n != null; n = n.Next)
					if (n.Value.TryGetTarget(out var v) && v is T tv) result.Add(tv);
			}
		}
		return result;
	}

	public static T? GetByKey<T>(string key) where T : VisualElement
	{
		lock (_gate)
			if (_byKey.TryGetValue(key, out var wr) && wr.TryGetTarget(out var v) && v is T tv)
				return tv;
		return null;
	}

	/*  
		Minimal API I recommend keeping
		// 1) Only if live (no queue)
		public static bool TryInvoke<T>(Action<T> action) where T : VisualElement;
		public static bool TryInvokeByKey<T>(string key, Action<T> action) where T : VisualElement;

		// 2) Run now or queue until target exists
		public static void Invoke<T>(Action<T> action) where T : VisualElement;
		public static void InvokeByKey<T>(string key, Action<T> action) where T : VisualElement;

		// 3) Await the instance, then act (use WaitAsync with a timeout at the callsite if needed)
		public static Task<T> WhenReady<T>() where T : VisualElement;
		public static Task<T> WhenReadyByKey<T>(string key) where T : VisualElement;
	*/

	public static bool TryInvoke<T>(Action<T> action) where T : VisualElement
	{
		var v = Get<T>();
		if (v is null) return false;
		RunOnUi(v, () => action((T)v));
		return true;
	}

	public static bool TryInvokeByKey<T>(string key, Action<T> action) where T : VisualElement
	{
		var v = GetByKey<T>(key);
		if (v is null) return false;
		RunOnUi(v, () => action(v));
		return true;
	}

	// Immediate fan-out to all current instances (no queueing)
	public static int TryInvokeAll<T>(Action<T> action) where T : VisualElement
	{
		var targets = GetAll<T>();
		foreach (var t in targets) RunOnUi(t, () => action(t));
		return targets.Count;
	}

	// If at least one T exists, invokes on the latest one; otherwise queues until one registers.
	// --- Invoke<T>: try immediate across assignable types; else queue under the *requested* T ---
	public static void Invoke<T>(Action<T> action) where T : VisualElement
	{
		var target = (VisualElement?)Get<T>();
		if (target is not null) { RunOnUi(target, () => action((T)target)); return; }

		lock (_gate)
		{
			if (!_pendingByType.TryGetValue(typeof(T), out var q))
				_pendingByType[typeof(T)] = q = new Queue<Action<VisualElement>>();
			q.Enqueue(ve => action((T)ve));
		}
	}

	// If key exists, invokes; otherwise queues until that key registers.
	public static void InvokeByKey<T>(string key, Action<T> action) where T : VisualElement
	{
		VisualElement? target = null;
		lock (_gate)
		{
			if (_byKey.TryGetValue(key, out var wr) && wr.TryGetTarget(out var v)) target = v;
			if (target is null)
			{
				if (!_pendingByKey.TryGetValue(key, out var q))
					_pendingByKey[key] = q = new Queue<Action<VisualElement>>();
				q.Enqueue(ve => action((T)ve));
			}
		}
		if (target is not null) RunOnUi(target, () => action((T)target));
	}

	// Immediate fan-out to all *current* instances of T (no queueing).
	public static int InvokeAll<T>(Action<T> action) where T : VisualElement
	{
		var targets = GetAll<T>();
		foreach (var t in targets) RunOnUi(t, () => action(t));
		return targets.Count;
	}

	public static Task<T> WhenReady<T>() where T : VisualElement
	{
		var existing = Get<T>();
		if (existing is T ready) return Task.FromResult(ready);

		var tcs = new TaskCompletionSource<T>();
		Invoke<T>(v => tcs.TrySetResult(v)); // queued or immediate
		return tcs.Task;
	}

	public static Task<T> WhenReadyByKey<T>(string key) where T : VisualElement
	{
		var existing = GetByKey<T>(key);
		if (existing is T ready) return Task.FromResult(ready);

		var tcs = new TaskCompletionSource<T>();
		InvokeByKey<T>(key, v => tcs.TrySetResult(v)); // queued or immediate
		return tcs.Task;
	}

	// --- Register: flush pending for *all base types* of v.GetType() that queued actions ---
	internal static void Register(VisualElement v, string? key)
	{
		Queue<Action<VisualElement>>? toRunExact = null;
		var toRunAssignable = new List<Action<VisualElement>>();

		lock (_gate)
		{
			var type = v.GetType();
			if (!_byType.TryGetValue(type, out var list))
				_byType[type] = list = new LinkedList<WeakReference<VisualElement>>();
			list.AddLast(new WeakReference<VisualElement>(v));
			if (list.Count > 64) Prune(list);

			if (!string.IsNullOrEmpty(key))
				_byKey[key!] = new WeakReference<VisualElement>(v);

			// exact-type queue
			if (_pendingByType.TryGetValue(type, out var q1) && q1.Count > 0)
			{
				toRunExact = new Queue<Action<VisualElement>>(q1);
				q1.Clear();
			}

			// base-type queues (any K where K.IsAssignableFrom(type))
			foreach (var (k, q) in _pendingByType)
				if (k != type && k.IsAssignableFrom(type) && q.Count > 0)
					while (q.Count > 0) toRunAssignable.Add(q.Dequeue());

			// key queue
			if (key != null && _pendingByKey.TryGetValue(key, out var q2) && q2.Count > 0)
				while (q2.Count > 0) toRunAssignable.Add(q2.Dequeue());
		}

		if (toRunExact != null)
			while (toRunExact.Count > 0) { var a = toRunExact.Dequeue(); RunOnUi(v, () => a(v)); }

		foreach (var a in toRunAssignable) RunOnUi(v, () => a(v));
	}

	internal static void Unregister(VisualElement v, string? key)
	{
		lock (_gate)
		{
			var type = v.GetType();
			if (_byType.TryGetValue(type, out var list))
			{
				for (var n = list.First; n != null;)
				{
					var next = n.Next;
					if (!n.Value.TryGetTarget(out var t) || ReferenceEquals(t, v))
						list.Remove(n);
					n = next;
				}
				if (list.Count == 0) _byType.Remove(type);
			}

			if (!string.IsNullOrEmpty(key) &&
				_byKey.TryGetValue(key!, out var wr) &&
				wr.TryGetTarget(out var cur) &&
				ReferenceEquals(cur, v))
			{
				_byKey.Remove(key!);
			}
		}
	}

	static void RunOnUi(VisualElement v, Action a)
	{
		void Safe() { try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); } }
		var d = v.Dispatcher;
		if (d?.IsDispatchRequired == true) d.Dispatch(Safe);
		else if (d != null) Safe();
		else MainThread.BeginInvokeOnMainThread(Safe);
	}

	static void Prune(LinkedList<WeakReference<VisualElement>> list)
	{
		for (var n = list.First; n != null;)
		{
			var next = n.Next;
			if (!n.Value.TryGetTarget(out _)) list.Remove(n);
			n = next;
		}
	}

	public static string DebugDump()
	{
		var sb = new System.Text.StringBuilder();
		lock (_gate)
		{
			sb.AppendLine("ViewDirectory:");
			foreach (var (type, list) in _byType)
			{
				int live = 0;
				for (var n = list.First; n != null; n = n.Next)
					if (n.Value.TryGetTarget(out _)) live++;
				sb.AppendLine($"  {type.Name}: {live} live entries");
			}
		}
		return sb.ToString();
	}
}

public sealed class RegisterInViewDirectoryBehavior : Behavior<VisualElement>
{
	public static readonly BindableProperty KeyProperty =
		BindableProperty.Create(nameof(Key), typeof(string), typeof(RegisterInViewDirectoryBehavior), default(string));

	public string? Key
	{
		get => (string?)GetValue(KeyProperty);
		set => SetValue(KeyProperty, value);
	}

	protected override void OnAttachedTo(VisualElement bindable)
	{
		base.OnAttachedTo(bindable);

		// If already attached to native pipeline, register now.
		if (bindable.Handler != null)
			ViewDirectory.Register(bindable, Key);

		bindable.HandlerChanged += OnHandlerChanged;   // attach → register
		bindable.HandlerChanging += OnHandlerChanging;  // detach → unregister
	}

	protected override void OnDetachingFrom(VisualElement bindable)
	{
		bindable.HandlerChanged -= OnHandlerChanged;
		bindable.HandlerChanging -= OnHandlerChanging;
		base.OnDetachingFrom(bindable);
	}

	void OnHandlerChanged(object? s, EventArgs e)
	{
		var v = (VisualElement)s!;
		if (v.Handler != null) ViewDirectory.Register(v, Key);
	}

	void OnHandlerChanging(object? s, HandlerChangingEventArgs e)
	{
		if (e.NewHandler == null) ViewDirectory.Unregister((VisualElement)s!, Key);
	}
}

public static class UI
{
	// Get the latest instance (or null). Feels like GetPageA().
	public static T? Get<T>() where T : VisualElement
		=> ViewDirectory.Get<T>();

	// Get by key (or null)
	public static T? Get<T>(string key) where T : VisualElement
		=> ViewDirectory.GetByKey<T>(key);

	// Now-or-drop (no queueing)
	public static bool Try<T>(Action<T> action) where T : VisualElement
		=> ViewDirectory.TryInvoke(action);
	public static bool Try<T>(string key, Action<T> action) where T : VisualElement
		=> ViewDirectory.TryInvokeByKey<T>(key, action);

	// Now-or-queue (runs later if needed)
	public static void Call<T>(Action<T> action) where T : VisualElement
		=> ViewDirectory.Invoke(action);
	public static void Call<T>(string key, Action<T> action) where T : VisualElement
		=> ViewDirectory.InvokeByKey(key, action);

	// Await the instance (for “must succeed or timeout” flows)
	public static Task<T> WhenReady<T>() where T : VisualElement
		=> ViewDirectory.WhenReady<T>();
	public static Task<T> WhenReady<T>(string key) where T : VisualElement
		=> ViewDirectory.WhenReadyByKey<T>(key);
}

//////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////

/*
	// List all windows:
	var windows = MauiIntrospectionExtensions.GetAllWindows();
	foreach (var w in windows)
		Console.WriteLine($"Window: {w.Title}  (Id {w.GetHashCode():X})");

	// List all realized pages:
	var pages = await MauiIntrospectionExtensions.GetAllPagesAsync();
	foreach (var p in pages)
		Console.WriteLine($"Page: {p.GetType().Name}  Title='{p.Title}'");

	// Just the topmost visible page per window:
	var visible = await MauiIntrospectionExtensions.GetVisiblePagesAsync();

public static class MauiIntrospectionExtensions
{
	/// <summary>Snapshot of all open MAUI windows.</summary>
	public static IReadOnlyList<Window> GetAllWindows()
		=> (Application.Current?.Windows?.ToList() ?? new List<Window>());

	/// <summary>Enumerate all *realized* pages across all windows.</summary>
	public static async Task<IReadOnlyList<Page>> GetAllPagesAsync()
	{
		return await MainThread.InvokeOnMainThreadAsync(() =>
		{
			var pages = new List<Page>();
			var seen = new HashSet<Page>();

			foreach (var w in GetAllWindows())
			{
				if (w?.Page is Page root)
					CollectPages(root, pages, seen);
			}

			return (IReadOnlyList<Page>)pages;
		});
	}

	/// <summary>Optionally: just the currently visible (top) page per window.</summary>
	public static async Task<IReadOnlyList<Page>> GetVisiblePagesAsync()
	{
		return await MainThread.InvokeOnMainThreadAsync(() =>
		{
			var result = new List<Page>();

			foreach (var w in GetAllWindows())
			{
				var root = w?.Page;
				switch (root)
				{
					case Shell shell:
						if (shell.Navigation?.ModalStack?.Count > 0)
							result.Add(shell.Navigation.ModalStack.Last());
						else if (shell.CurrentPage is Page cp)
							result.Add(cp);
						break;

					case NavigationPage nav:
						if (nav.Navigation?.ModalStack?.Count > 0)
							result.Add(nav.Navigation.ModalStack.Last());
						else if (nav.CurrentPage is Page np)
							result.Add(np);
						break;

					default:
						if (root is Page p) result.Add(p);
						break;
				}
			}

			return (IReadOnlyList<Page>)result;
		});
	}

	// ---- helpers ----

	static void CollectPages(Page page, IList<Page> output, ISet<Page> seen)
	{
		if (page is null || !seen.Add(page)) return;
		output.Add(page);

		switch (page)
		{
			case Shell shell:
				// Realized navigation stacks for the *current* section:
				AddNavStacks(shell.Navigation, output, seen);

				// Optional: walk Shell structure to include realized ShellContent pages.
				// NOTE: Accessing ShellContent.Content may instantiate the page if not created yet.
				foreach (var item in shell.Items)
					foreach (var section in item.Items)
						foreach (var content in section.Items)
							if (content is ShellContent sc && sc.Content is Page scPage)
								CollectPages(scPage, output, seen);
				break;

			case NavigationPage nav:
				AddNavStacks(nav?.Navigation, output, seen);
				break;

			case TabbedPage tabs:
				foreach (var child in tabs.Children)
					CollectPages(child, output, seen);
				break;

			case FlyoutPage flyout:
				if (flyout.Flyout is Page f) CollectPages(f, output, seen);
				if (flyout.Detail is Page d) CollectPages(d, output, seen);
				break;
		}
	}

	static void AddNavStacks(INavigation? nav, IList<Page> output, ISet<Page> seen)
	{
		if (nav is null) return;

		foreach (var p in nav.NavigationStack)
			CollectPages(p, output, seen);

		foreach (var p in nav.ModalStack)
			CollectPages(p, output, seen);
	}
}
*/
