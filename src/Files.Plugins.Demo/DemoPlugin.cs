// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using Files.Plugins;

namespace Files.Plugins.Demo;

/// <summary>
/// Reference plugin proving the loading chain: appears in the file context menu when a single
/// file is selected and opens it in Notepad.
/// </summary>
public sealed class DemoPlugin : IFilesPlugin, IContextMenuContributor
{
	public string Id => "files.demo";
	public string Name => "Demo Plugin";
	public string Description => "Adds an 'Open in Notepad' entry to the file context menu.";
	public string Version => "1.0";

	public Task InitializeAsync(IFilesPluginHost host, CancellationToken cancellationToken)
	{
		host.LogInformation(Id, $"Demo plugin loaded (host {host.HostVersion}).");
		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
		=> Task.CompletedTask;

	public IReadOnlyList<PluginContextMenuItem> GetMenuItems(PluginContextMenuContext context)
	{
		if (context.SelectedPaths.Count != 1)
			return [];

		var path = context.SelectedPaths[0];

		return
		[
			new PluginContextMenuItem()
			{
				Text = $"Open in Notepad ({Name})",
				Glyph = "\uE70F",
				IsEnabled = File.Exists(path),
				Execute = () =>
				{
					Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
					return Task.CompletedTask;
				},
			},
		];
	}
}
