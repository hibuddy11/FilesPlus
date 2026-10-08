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
	private const int MaxEntries = 100_000;

	public static List<SearchEntry> Scan(string root)
	{
		var entries = new List<SearchEntry>();
		var pending = new Stack<(string Path, string Relative)>();
		pending.Push((root, string.Empty));

		while (pending.Count > 0 && entries.Count < MaxEntries)
		{
			var (directory, relative) = pending.Pop();

			try
			{
				var info = new DirectoryInfo(directory);

				foreach (var sub in info.EnumerateDirectories())
				{
					if (entries.Count >= MaxEntries)
						break;

					// Reparse points (junctions, symlinks) would revisit earlier trees
					if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
						continue;

					entries.Add(ToEntry(sub, relative, isDirectory: true));
					pending.Push((sub.FullName, Combine(relative, sub.Name)));
				}

				foreach (var file in info.EnumerateFiles())
				{
					if (entries.Count >= MaxEntries)
						break;

					entries.Add(ToEntry(file, relative, isDirectory: false));
				}
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
			{
				// Unreadable or vanished directory: skip it and keep scanning siblings
			}
		}

		return entries;
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
