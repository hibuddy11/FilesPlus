// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;

namespace Files.Plugins.Todo;

/// <summary>
/// Todo plugin: a lightweight capture list backed by plain todo.txt files with screenshot
/// attachments. Exposes a tool window the host opens from the sidebar's Todo shortcut.
/// </summary>
public sealed class TodoPlugin : IFilesPlugin, IToolWindowProvider
{
	private const string PluginId = "files.todo";

	private IFilesPluginHost host = null!;
	private TodoStore store = null!;
	private TodoWindow? window;

	public string Id => PluginId;
	public string Name => "Todo";
	public string Description => "轻量待办清单（todo.txt 格式 + 截图附件）";
	public string Version => "1.0";

	public string Title => Name;
	public string Glyph => "\uE9D5";

	public async Task InitializeAsync(IFilesPluginHost host, CancellationToken cancellationToken)
	{
		this.host = host;
		store = new TodoStore(host, PluginId);
		await store.EnsureReadyAsync();
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		if (window is not null)
		{
			try
			{
				window.Close();
			}
			catch (Exception ex)
			{
				host.LogError(PluginId, "Failed to close the Todo window during shutdown.", ex);
			}
			window = null;
		}

		return Task.CompletedTask;
	}

	// Called on the UI thread by the host (sidebar shortcut).
	public void ShowWindow()
	{
		try
		{
			if (window is null)
			{
				window = new TodoWindow(store, host, PluginId);
				window.Closed += (_, _) => window = null;
				window.Activate();
			}
			else
			{
				// Re-apply so a theme change made after the window opened is picked up.
				window.ApplyHostTheme();
				window.Activate();
				_ = window.ReloadAsync();
			}
		}
		catch (Exception ex)
		{
			host.LogError(PluginId, "Failed to open the Todo window.", ex);
		}
	}
}
