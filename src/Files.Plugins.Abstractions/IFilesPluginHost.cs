// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins;

/// <summary>
/// Services the host exposes to plugins. Intentionally minimal: plugins should be self-contained
/// and must not reach back into the host's internals.
/// </summary>
public interface IFilesPluginHost
{
	/// <summary>Version of the hosting app.</summary>
	string HostVersion { get; }

	/// <summary>Directory where the plugin may store its own data (e.g. settings, caches).</summary>
	string GetPluginDataDirectory(string pluginId);

	/// <summary>Writes an informational message to the app log.</summary>
	void LogInformation(string pluginId, string message);

	/// <summary>Writes an error to the app log.</summary>
	void LogError(string pluginId, string message, Exception? exception = null);

	/// <summary>Opens a file or folder with the system default handler (shell open).</summary>
	void OpenPath(string path);
}
