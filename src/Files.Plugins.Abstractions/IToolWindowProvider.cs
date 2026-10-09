// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins;

/// <summary>
/// Contribution point: a plugin-provided tool window that the host can open on behalf of the user
/// (e.g. from a sidebar shortcut). Implement this interface on the plugin class alongside
/// <see cref="IFilesPlugin"/>. <see cref="ShowWindow"/> is always called on the UI thread.
/// </summary>
public interface IToolWindowProvider
{
	/// <summary>Display title of the tool window (used by host UI such as tooltips).</summary>
	string Title { get; }

	/// <summary>Segoe MDL2 Assets glyph representing the tool, e.g. "\uE9D5".</summary>
	string Glyph { get; }

	/// <summary>Shows the tool window, creating it if needed, and brings it to the foreground.</summary>
	void ShowWindow();
}
