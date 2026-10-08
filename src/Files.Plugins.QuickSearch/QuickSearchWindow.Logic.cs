// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Files.Plugins.QuickSearch;

internal sealed partial class QuickSearchWindow
{
	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		debounceTimer.Stop();
		debounceTimer.Start();
	}

	private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Escape)
		{
			window.Close();
			e.Handled = true;
		}
		else if (e.Key is VirtualKey.Enter)
		{
			OpenResult(resultList.SelectedItem as SearchEntry ?? results.FirstOrDefault());
			e.Handled = true;
		}
		else if (e.Key is VirtualKey.Down && results.Count > 0)
		{
			resultList.Focus(FocusState.Programmatic);
			resultList.SelectedIndex = 0;
			e.Handled = true;
		}
	}

	private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		=> OpenResult(resultList.SelectedItem as SearchEntry);

	private async Task RunFilterAsync(string query)
	{
		var version = ++filterVersion;
		var source = entries;

		var filtered = await Task.Run(() => Filter(source, query));

		// A newer keystroke superseded this pass
		if (version != filterVersion)
			return;

		results = filtered;
		resultList.ItemsSource = filtered;
		resultList.SelectedIndex = filtered.Count > 0 ? 0 : -1;
		statusText.Text = string.IsNullOrWhiteSpace(query)
			? $"已索引 {entries.Count} 项"
			: $"匹配 {filtered.Count} 项（已索引 {entries.Count} 项）";
	}

	private static List<SearchEntry> Filter(List<SearchEntry> source, string query)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return source
				.OrderBy(x => x.IsDirectory ? 0 : 1)
				.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.Take(MaxResults)
				.ToList();
		}

		var matches = new List<(SearchEntry Entry, int Score)>(capacity: 256);
		foreach (var entry in source)
		{
			if (entry.Score(query) is { } score)
				matches.Add((entry, score));
		}

		return matches
			.OrderByDescending(x => x.Score)
			.ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
			.Take(MaxResults)
			.Select(x => x.Entry)
			.ToList();
	}

	private void OpenResult(SearchEntry? entry)
	{
		if (entry is null)
			return;

		try
		{
			host.OpenPath(entry.FullPath);
			window.Close();
		}
		catch (Exception ex)
		{
			host.LogError("files.quicksearch", $"Failed to open '{entry.FullPath}'.", ex);
		}
	}
}
