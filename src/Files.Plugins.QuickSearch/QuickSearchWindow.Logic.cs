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
			OpenResult(SelectedEntry ?? results.FirstOrDefault());
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
		=> OpenResult(SelectedEntry);

	private SearchEntry? SelectedEntry
		=> resultList.SelectedIndex >= 0 && resultList.SelectedIndex < results.Count
			? results[resultList.SelectedIndex]
			: null;

	private async Task RunFilterAsync(string query)
	{
		var version = ++filterVersion;
		var source = entries;

		List<SearchEntry> filtered;
		try
		{
			filtered = await Task.Run(() => Filter(source, query));
		}
		catch (Exception ex)
		{
			// Scoring lazily touches the pinyin library for the first time; a load failure must
			// not surface as an unobserved task exception (the host exits on those).
			host.LogError("files.quicksearch", "Filtering failed.", ex);
			return;
		}

		// A newer keystroke superseded this pass
		if (version != filterVersion)
			return;

		try
		{
			results = filtered;
			// Bind plain strings: plugin-assembly element types can fail to marshal across the
			// assembly-load-context boundary when assigned to ItemsSource (E_INVALIDARG).
			resultList.ItemsSource = filtered.Select(x => x.ToString()).ToList();
			resultList.SelectedIndex = filtered.Count > 0 ? 0 : -1;
			statusText.Text = string.IsNullOrWhiteSpace(query)
				? $"已索引 {entries.Count} 项"
				: $"匹配 {filtered.Count} 项（已索引 {entries.Count} 项）";
		}
		catch (Exception ex)
		{
			host.LogError("files.quicksearch", "Failed to update the result list.", ex);
		}
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
