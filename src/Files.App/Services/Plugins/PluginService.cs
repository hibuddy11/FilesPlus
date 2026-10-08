// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Files.Plugins;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Files.App.Services.Plugins;

/// <summary>
/// Loads, initializes and shuts down runtime plugins, and aggregates their contributions.
///
/// Loading uses reflection only on the plugin assemblies themselves (type discovery +
/// activation), which are external, non-trimmed DLLs; the host's own code stays free of
/// runtime reflection. This works because the unpackaged distribution ships as IL with JIT;
/// it would NOT work under the AOT publish profiles.
/// </summary>
internal sealed class PluginService : IPluginService, IFilesPluginHost
{
	private readonly IUserSettingsService userSettingsService;

	private List<PluginRecord> plugins = [];
	private int initializationState; // 0 = not started, 1 = initializing/initialized

	public static string PluginsDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

	public IReadOnlyList<PluginRecord> Plugins => plugins;

	public string HostVersion => typeof(PluginService).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

	public PluginService(IUserSettingsService userSettingsService)
	{
		this.userSettingsService = userSettingsService;
	}

	public async Task InitializeAsync(CancellationToken cancellationToken)
	{
		// Only one initialization per process; a second call is a no-op
		if (Interlocked.Exchange(ref initializationState, 1) != 0)
			return;

		var records = DiscoverPlugins();
		plugins = records;

		foreach (var record in records.Where(x => x.IsEnabled && x.Instance is not null))
		{
			try
			{
				await record.Instance!.InitializeAsync(this, cancellationToken);
			}
			catch (Exception ex)
			{
				MarkFailed(record, ex.Message);
				App.Logger?.LogWarning(ex, $"Plugin '{record.Id}' failed to initialize and is disabled for this session.");
			}
		}

		var loaded = records.Count(x => x.IsEnabled && x.Instance is not null && x.LoadError is null);
		if (records.Count > 0)
			App.Logger?.LogInformation("[Plugins] {Loaded}/{Total} plugins loaded from {Directory}", loaded, records.Count, PluginsDirectory);
	}

	public async Task ShutdownAsync(CancellationToken cancellationToken)
	{
		foreach (var record in plugins.Where(x => x.IsEnabled && x.Instance is not null && x.LoadError is null))
		{
			try
			{
				await record.Instance!.ShutdownAsync(cancellationToken);
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, $"Plugin '{record.Id}' failed to shut down cleanly.");
			}
		}
	}

	public IReadOnlyList<PluginContextMenuItem> GetContextMenuItems(PluginContextMenuContext context)
	{
		var items = new List<PluginContextMenuItem>();

		foreach (var record in plugins)
		{
			if (!record.IsEnabled || record.LoadError is not null || record.Instance is not IContextMenuContributor contributor)
				continue;

			try
			{
				items.AddRange(contributor.GetMenuItems(context));
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, $"Plugin '{record.Id}' failed to build context menu items.");
			}
		}

		return items;
	}

	public string GetPluginDataDirectory(string pluginId)
	{
		var path = Path.Combine(AppDataCompat.LocalFolderPath, "Plugins", pluginId);
		Directory.CreateDirectory(path);
		return path;
	}

	public void LogInformation(string pluginId, string message)
		=> App.Logger?.LogInformation("[Plugin {PluginId}] {Message}", pluginId, message);

	public void LogError(string pluginId, string message, Exception? exception = null)
		=> App.Logger?.LogError(exception, "[Plugin {PluginId}] {Message}", pluginId, message);

	public void OpenPath(string path)
	{
		if (string.IsNullOrEmpty(path))
			return;

		Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
	}

	private List<PluginRecord> DiscoverPlugins()
	{
		var records = new List<PluginRecord>();

		if (!Directory.Exists(PluginsDirectory))
			return records;

		var disabled = userSettingsService.GeneralSettingsService.DisabledPlugins ?? [];

		foreach (var pluginPath in Directory.EnumerateFiles(PluginsDirectory, "*.dll"))
		{
			try
			{
				var loadContext = new PluginLoadContext(pluginPath);
				var assembly = loadContext.LoadFromAssemblyPath(pluginPath);

				// Plugin assemblies are external and not trimmed, so type scanning is safe there
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = [.. ex.Types.WhereNotNull()];
				}

				foreach (var type in types)
				{
					if (type is not { IsAbstract: false, IsInterface: false } || !typeof(IFilesPlugin).IsAssignableFrom(type))
						continue;

					var plugin = (IFilesPlugin)Activator.CreateInstance(type)!;
					records.Add(new PluginRecord(
						plugin.Id,
						plugin.Name,
						plugin.Description,
						plugin.Version,
						pluginPath,
						!disabled.Contains(plugin.Id),
						plugin,
						null));
				}
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, $"Failed to load plugin assembly '{pluginPath}'.");
			}
		}

		return records;
	}

	private void MarkFailed(PluginRecord record, string error)
	{
		var index = plugins.FindIndex(x => x.Id == record.Id && x.AssemblyPath == record.AssemblyPath);
		if (index >= 0)
			plugins[index] = record with { IsEnabled = false, LoadError = error };
	}
}
