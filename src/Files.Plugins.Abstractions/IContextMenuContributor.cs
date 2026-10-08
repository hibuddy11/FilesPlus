// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins;

/// <summary>
/// Context describing the invocation a context menu is being built for.
/// </summary>
public sealed class PluginContextMenuContext
{
	/// <summary>The directory the current page is browsing, when known.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>Full paths of the selected items; empty when the menu targets the background.</summary>
	public IReadOnlyList<string> SelectedPaths { get; init; } = [];

	/// <summary>Display names of the selected items; empty when the menu targets the background.</summary>
	public IReadOnlyList<string> SelectedNames { get; init; } = [];
}

/// <summary>
/// A menu item contributed by a plugin. Mapped by the host onto the app's context menu.
/// </summary>
public sealed class PluginContextMenuItem
{
	public required string Text { get; init; }

	/// <summary>Segoe MDL2 Assets glyph, e.g. "\uE8C8".</summary>
	public string? Glyph { get; init; }

	public bool IsEnabled { get; init; } = true;

	/// <summary>Invoked on the UI thread when the item is clicked.</summary>
	public required Func<Task> Execute { get; init; }
}

/// <summary>
/// Contribution point: entries appended to the file-area context menu (both item and background
/// menus). Implement this interface on the plugin class alongside <see cref="IFilesPlugin"/>.
/// </summary>
public interface IContextMenuContributor
{
	IReadOnlyList<PluginContextMenuItem> GetMenuItems(PluginContextMenuContext context);
}
