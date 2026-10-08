// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Files.App.Services.Plugins;

/// <summary>
/// Isolates a plugin assembly from the host and from other plugins. Dependencies resolve in
/// order: assemblies already loaded in the default context (the contract assembly MUST unify
/// with the host's instance or type checks fail), the plugin's own directory (via its
/// deps.json when present), then plain probing of the plugins directory.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
	private readonly AssemblyDependencyResolver resolver;
	private readonly string pluginDirectory;

	public PluginLoadContext(string pluginPath)
		: base(name: Path.GetFileNameWithoutExtension(pluginPath))
	{
		resolver = new AssemblyDependencyResolver(pluginPath);
		pluginDirectory = Path.GetDirectoryName(pluginPath) ?? throw new ArgumentException("The plugin path has no directory.", nameof(pluginPath));
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is null)
			return null;

		// Unify with the host: never load a second copy of an assembly the host already has.
		var loaded = Default.Assemblies.FirstOrDefault(x => x.GetName().Name == assemblyName.Name);
		if (loaded is not null)
			return loaded;

		var path = resolver.ResolveAssemblyToPath(assemblyName)
			?? ProbePluginDirectory(assemblyName.Name);

		return path is not null ? LoadFromAssemblyPath(path) : null;
	}

	private string? ProbePluginDirectory(string assemblyName)
	{
		var candidate = Path.Combine(pluginDirectory, assemblyName + ".dll");
		return File.Exists(candidate) ? candidate : null;
	}
}
