// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.Plugins.QuickSearch;

/// <summary>
/// Walks a directory tree into the in-memory search index. Skips junctions/symlinks to avoid
/// cycles, ignores unreadable locations, and stops at the entry cap.
/// </summary>
internal static class DirectoryScanner
{
	public const int MaxEntries = 500_000;

	/// <summary>
	/// Lazily walks the tree so the caller can merge entries into the live index while the scan
	/// is still running; the caller enforces <see cref="MaxEntries"/>.
	/// </summary>
	public static IEnumerable<SearchEntry> Scan(string root)
	{
		var pending = new Stack<(string Path, string Relative)>();
		pending.Push((root, string.Empty));

		while (pending.Count > 0)
		{
			var (directory, relative) = pending.Pop();

			// Iterator methods cannot yield inside try/catch, so each directory's children are
			// collected safely first and yielded afterwards (one batch per directory).
			List<SearchEntry> children;
			List<(string Path, string Relative)> subDirectories;
			try
			{
				(children, subDirectories) = EnumerateDirectory(directory, relative);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
			{
				// Unreadable or vanished directory: skip it and keep scanning siblings
				continue;
			}

			foreach (var child in children)
				yield return child;

			foreach (var sub in subDirectories)
				pending.Push(sub);
		}
	}

	private static (List<SearchEntry> Children, List<(string Path, string Relative)> SubDirectories) EnumerateDirectory(string directory, string relative)
	{
		var children = new List<SearchEntry>();
		var subDirectories = new List<(string, string)>();
		var info = new DirectoryInfo(directory);

		foreach (var sub in info.EnumerateDirectories())
		{
			// Reparse points (junctions, symlinks) would revisit earlier trees
			if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
				continue;

			children.Add(ToEntry(sub, relative, isDirectory: true));
			subDirectories.Add((sub.FullName, Combine(relative, sub.Name)));
		}

		foreach (var file in info.EnumerateFiles())
			children.Add(ToEntry(file, relative, isDirectory: false));

		return (children, subDirectories);
	}

	private static SearchEntry ToEntry(FileSystemInfo info, string parentRelative, bool isDirectory)
		=> new()
		{
			Name = info.Name,
			FullPath = info.FullName,
			RelativePath = Combine(parentRelative, info.Name),
			IsDirectory = isDirectory,
		};

	private static string Combine(string parentRelative, string name)
		=> parentRelative.Length is 0 ? name : $"{parentRelative}\\{name}";
}
