// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Files.Plugins.QuickSearch;

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

	private List<SearchEntry> entries = [];
	private List<SearchEntry> results = [];
	private int filterVersion;

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

		var root = new Grid();
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		root.Children.Add(searchBox);
		root.Children.Add(resultList);
		root.Children.Add(statusText);
		Grid.SetRow(resultList, 1);
		Grid.SetRow(statusText, 2);
		window.Content = root;

		debounceTimer = window.DispatcherQueue.CreateTimer();
		debounceTimer.Interval = TimeSpan.FromMilliseconds(100);
		debounceTimer.Tick += (_, _) =>
		{
			debounceTimer.Stop();
			_ = RunFilterAsync(searchBox.Text);
		};
		window.Closed += (_, _) => debounceTimer.Stop();
	}

	public void Show()
	{
		// Focus after the window is active; focusing before activation is unreliable
		window.Activated += (_, _) => searchBox.Focus(FocusState.Programmatic);
		window.Activate();

		statusText.Text = "正在建立索引…";
		_ = Task.Run(() =>
		{
			try
			{
				var scanned = DirectoryScanner.Scan(rootDirectory);
				window.DispatcherQueue.TryEnqueue(() =>
				{
					entries = scanned;
					_ = RunFilterAsync(searchBox.Text);
				});
			}
			catch (Exception ex)
			{
				window.DispatcherQueue.TryEnqueue(() => host.LogError("files.quicksearch", "Directory scan failed.", ex));
			}
		});
	}
}
