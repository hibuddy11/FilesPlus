// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;

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

	private readonly List<SearchEntry> entries = [];
	private List<SearchEntry> results = [];
	private volatile bool isScanning;
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
		isScanning = true;
		_ = Task.Run(ScanAndStreamResults);
	}

	/// <summary>
	/// Streams the scan into the index in batches so results are searchable long before the
	/// whole tree has been walked.
	/// </summary>
	private void ScanAndStreamResults()
	{
		try
		{
			var batch = new List<SearchEntry>(capacity: 4096);
			var lastFlush = Stopwatch.StartNew();
			var scanned = 0;

			foreach (var entry in DirectoryScanner.Scan(rootDirectory))
			{
				if (scanned >= DirectoryScanner.MaxEntries)
					break;

				scanned++;
				batch.Add(entry);

				if (batch.Count < 4096 && lastFlush.ElapsedMilliseconds < 400)
					continue;

				FlushBatch(batch);
				batch = [];
				lastFlush.Restart();
			}

			if (batch.Count > 0)
				FlushBatch(batch);
		}
		catch (Exception ex)
		{
			window.DispatcherQueue.TryEnqueue(() => host.LogError("files.quicksearch", "Directory scan failed.", ex));
			return;
		}

		window.DispatcherQueue.TryEnqueue(() =>
		{
			isScanning = false;
			_ = RunFilterAsync(searchBox.Text);
		});
	}

	private void FlushBatch(List<SearchEntry> batch)
	{
		// Hand the UI thread a snapshot it owns
		var chunk = batch.ToArray();
		window.DispatcherQueue.TryEnqueue(() =>
		{
			var remaining = DirectoryScanner.MaxEntries - entries.Count;
			if (remaining <= 0)
				return;

			entries.AddRange(chunk.Length <= remaining ? chunk : chunk[..remaining]);
			_ = RunFilterAsync(searchBox.Text);
		});
	}
}
