// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Files.Plugins.QuickSearch;

internal sealed partial class QuickSearchWindow
{
	private int filterRunId;

	/// <summary>
	/// Filters the current snapshot on a worker thread; only the latest run wins.
	/// </summary>
	private async void RequestFilterRun(string query)
	{
		var version = ++filterRunId;
		var snapshot = index.Snapshot();
		MatchResult result;
		try
		{
			result = await Task.Run(() => Filter(snapshot, query));
		}
		catch (Exception ex)
		{
			// Filtering may first touch the pinyin library; a load failure must not surface as an
			// unobserved task exception (the host exits on those).
			host.LogError("files.quicksearch", "Filtering failed.", ex);
			return;
		}

		// A newer filter superseded this pass
		if (version != filterRunId)
			return;

		UpdateResults(result, query);
	}

	/// <summary>Applies a finished filter result to the UI.</summary>
	private void UpdateResults(MatchResult result, string query)
	{
		latestResult = result;
		try
		{
			// Bind plain strings: plugin-assembly element types can fail to marshal across the
			// assembly-load-context boundary when assigned to ItemsSource (E_INVALIDARG).
			resultList.ItemsSource = result.Indices.Select(DisplayEntry).ToList();
			resultList.SelectedIndex = result.Count > 0 ? 0 : -1;
		}
		catch (Exception ex)
		{
			host.LogError("files.quicksearch", "Failed to update the result list.", ex);
			return;
		}

		var indexed = index.Count;
		statusText.Text = query.Length is 0
			? (result.Count > 0
				? $"显示前 {result.Count} 项（已索引 {indexed:N0} 项）"
				: $"已索引 {indexed:N0} 项")
			: (result.Count > 0
				? $"匹配 {result.Count:N0} 项，显示前 {Math.Min(result.Count, MaxResults)} 项（已索引 {indexed:N0} 项）"
				: $"无匹配（已索引 {indexed:N0} 项）");
		if (isScanning)
			statusText.Text += "，扫描中…";
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		RequestFilter();
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
			OpenResult(SelectedEntry);
			e.Handled = true;
		}
		else if (e.Key is VirtualKey.Down && latestResult.Count > 0)
		{
			resultList.Focus(FocusState.Programmatic);
			resultList.SelectedIndex = 0;
			e.Handled = true;
		}
	}

	private int? SelectedEntry
		=> resultList.SelectedIndex >= 0 && resultList.SelectedIndex < latestResult.Indices.Length
			? latestResult.Indices[resultList.SelectedIndex]
			: null;

	private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		=> OpenResult(SelectedEntry);

	private void OpenResult(int? entryIndex)
	{
		if (entryIndex is not { } entry)
			return;

		try
		{
			host.OpenPath(index.GetFullPath(entry));
			window.Close();
		}
		catch (Exception ex)
		{
			host.LogError("files.quicksearch", $"Failed to open '{index.GetRelativePath(entry)}'.", ex);
		}
	}
}
