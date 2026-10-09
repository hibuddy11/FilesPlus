// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;

namespace Files.App.Services.Plugins;

/// <summary>
/// Hosts the runtime-loadable plugins found in the app's <c>plugins</c> directory.
/// </summary>
public interface IPluginService
{
	/// <summary>All discovered plugins, including disabled and failed ones.</summary>
	IReadOnlyList<PluginRecord> Plugins { get; }

	/// <summary>Loads plugin assemblies and initializes enabled plugins. Safe to call once; failures are per-plugin.</summary>
	Task InitializeAsync(CancellationToken cancellationToken);

	/// <summary>Notifies enabled plugins that the app is shutting down.</summary>
	Task ShutdownAsync(CancellationToken cancellationToken);

	/// <summary>Aggregated context-menu contributions from all enabled plugins. Failures are logged and skipped.</summary>
	IReadOnlyList<PluginContextMenuItem> GetContextMenuItems(PluginContextMenuContext context);

	/// <summary>Tool windows contributed by all enabled plugins, in load order. Failures are logged and skipped.</summary>
	IReadOnlyList<IToolWindowProvider> GetToolWindowProviders();
}

/// <summary>
/// A plugin discovered by the host, with its load/enable state.
/// </summary>
public sealed record PluginRecord(
	string Id,
	string Name,
	string Description,
	string Version,
	string AssemblyPath,
	bool IsEnabled,
	IFilesPlugin? Instance,
	string? LoadError);
