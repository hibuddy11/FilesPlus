// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Files.Plugins.QuickSearch;

/// <summary>Flat filter result: global entry indices in display order plus the total match count.</summary>
internal readonly record struct MatchResult(int[] Indices, int Count)
{
	public static readonly MatchResult Empty = new(Array.Empty<int>(), 0);
}

/// <summary>
/// The quick-search window: scans the target directory into memory once (Lertaro-style in-memory
/// index) and filters it live with fzf/pinyin matching as the user types.
/// Enter opens the selection with the shell, Esc closes the window.
/// </summary>
internal sealed partial class QuickSearchWindow
{
	private const int MaxResults = 100;

	private readonly IFilesPluginHost host;
	private readonly string rootDirectory;
	private readonly Window window;
	private readonly TextBox searchBox;
	private readonly ListView resultList;
	private readonly TextBlock statusText;
	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer debounceTimer;

	private SearchIndex index = null!;
	private MatchResult latestResult = MatchResult.Empty;
	private volatile bool isScanning;
	private CancellationTokenSource? scanCts;

	public QuickSearchWindow(IFilesPluginHost host, string rootDirectory)
	{
		this.host = host;
		this.rootDirectory = rootDirectory;

		window = new()
		{
			Title = $"快速搜索 - {Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar))}",
		};
		try
		{
			window.AppWindow.Resize(new(900, 560));
		}
		catch (Exception)
		{
			// The default size is acceptable when resizing fails
		}
		// Extend content into the title bar so its background follows the app theme.
		window.ExtendsContentIntoTitleBar = true;

		searchBox = new TextBox()
		{
			FontSize = 16,
			PlaceholderText = "输入以筛选（模糊/拼音首字母/全拼），回车打开，Esc 关闭",
			Margin = new(12),
		};
		searchBox.TextChanged += SearchBox_TextChanged;
		searchBox.KeyDown += SearchBox_KeyDown;

		resultList = new ListView()
		{
			Margin = new(12, 0, 12, 0),
			SelectionMode = ListViewSelectionMode.Single,
			IsItemClickEnabled = false,
		};
		resultList.DoubleTapped += ResultList_DoubleTapped;

		statusText = new TextBlock() { Margin = new(12, 6, 12, 10), Opacity = 0.7 };

		var root = new Grid { Margin = new Thickness(0, 32, 0, 0) };
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		root.Children.Add(searchBox);
		root.Children.Add(resultList);
		root.Children.Add(statusText);
		Grid.SetRow(resultList, 1);
		Grid.SetRow(statusText, 2);
		window.Content = root;
		ApplyHostTheme();

		index = new SearchIndex(rootDirectory);
		index.Published += Index_Published;

		debounceTimer = window.DispatcherQueue.CreateTimer();
		debounceTimer.Interval = TimeSpan.FromMilliseconds(100);
		debounceTimer.Tick += (_, _) =>
		{
			debounceTimer.Stop();
			RequestFilterRun(searchBox.Text);
		};
		window.Closed += (_, _) =>
		{
			debounceTimer.Stop();
			scanCts?.Cancel();
		};
	}

	public void Show()
	{
		// Focus after the window is active; focusing before activation is unreliable
		window.Activated += (_, _) => searchBox.Focus(FocusState.Programmatic);
		window.Activate();

		statusText.Text = "正在建立索引…";
		isScanning = true;
		scanCts = new CancellationTokenSource();
		var token = scanCts.Token;
		_ = Task.Run(() => ScanIntoIndex(token));
	}

	// Without this the window follows the OS light/dark setting; match the app's appearance
	// setting instead so plugin windows look consistent with the main window.
	private void ApplyHostTheme()
	{
		if (!Enum.TryParse(host.GetAppTheme(), out ElementTheme theme))
			theme = ElementTheme.Default;

		if (window.Content is FrameworkElement root)
			root.RequestedTheme = theme;

		if (window.AppWindow?.TitleBar is not { } titleBar)
			return;

		// Content extends into the title bar: keep every button backdrop transparent and only
		// pin the glyph color for the explicit light/dark settings.
		titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
		titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
		titleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
		titleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
		switch (theme)
		{
			case ElementTheme.Light:
				titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
				break;
			case ElementTheme.Dark:
				titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
				break;
		}
	}

	/// <summary>
	/// Walks the whole tree into the chunked index on a worker thread; each published chunk makes
	/// its entries searchable immediately through the Published callback.
	/// </summary>
	private void ScanIntoIndex(CancellationToken token)
	{
		try
		{
			DirectoryScanner.ScanInto(rootDirectory, index, token);
		}
		catch (Exception ex)
		{
			window.DispatcherQueue.TryEnqueue(() => host.LogError("files.quicksearch", "Directory scan failed.", ex));
			return;
		}

		window.DispatcherQueue.TryEnqueue(() =>
		{
			isScanning = false;
			RequestFilterRun(searchBox.Text);
		});
	}

	/// <summary>Runs the empty filter once per published chunk while scanning.</summary>
	private void Index_Published()
		=> window.DispatcherQueue.TryEnqueue(() =>
		{
			if (searchBox.Text.Length is 0)
				UpdateResults(MatchResult.Empty, string.Empty);
		});

	/// <summary>Schedules a debounced filter run from the current search box text.</summary>
	private void RequestFilter()
	{
		debounceTimer.Stop();
		debounceTimer.Start();
	}
}
