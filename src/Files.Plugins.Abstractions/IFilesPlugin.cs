// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins;

/// <summary>
/// A runtime-loadable plugin hosted by the app. Implementations live in separate DLLs placed in
/// the app's <c>plugins</c> directory and are discovered and activated by the host at startup.
/// A single DLL may export more than one plugin type.
/// </summary>
public interface IFilesPlugin
{
	/// <summary>Stable machine identifier used for logging and the enabled/disabled setting.</summary>
	string Id { get; }

	/// <summary>Display name shown in the plugin management UI.</summary>
	string Name { get; }

	/// <summary>Short human-readable description.</summary>
	string Description { get; }

	/// <summary>Plugin version, free-form text.</summary>
	string Version { get; }

	/// <summary>Called once after the plugin is instantiated. Throwing disables the plugin for the session.</summary>
	Task InitializeAsync(IFilesPluginHost host, CancellationToken cancellationToken);

	/// <summary>Called when the app is shutting down. Keep it fast; the host does not wait long.</summary>
	Task ShutdownAsync(CancellationToken cancellationToken);
}
