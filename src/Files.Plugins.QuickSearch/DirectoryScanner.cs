// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.Plugins.QuickSearch;

/// <summary>
/// Walks a directory tree straight into the search index. Skips junctions/symlinks to avoid
/// cycles, ignores unreadable locations, and stops at the index entry cap.
/// </summary>
internal static class DirectoryScanner
{
	public static void ScanInto(string root, SearchIndex index, CancellationToken token)
	{
		var pending = new Stack<(string Path, int ParentIndex)>();
		pending.Push((Path.TrimEndingDirectorySeparator(root), -1));

		while (pending.Count > 0 && !token.IsCancellationRequested && index.Count < SearchIndex.MaxEntries)
		{
			var (directory, parentIndex) = pending.Pop();

			List<(string FullName, int SelfIndex)>? subDirectories;
			try
			{
				var info = new DirectoryInfo(directory);
				subDirectories = [];

				foreach (var sub in info.EnumerateDirectories())
				{
					// Reparse points (junctions, symlinks) would revisit earlier trees
					if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
						continue;

					var selfIndex = index.Append(sub.Name, parentIndex, isDirectory: true, SearchIndex.ComputeAlias(sub.Name));
					if (selfIndex >= 0)
						subDirectories.Add((sub.FullName, selfIndex));
				}

				foreach (var file in info.EnumerateFiles())
					index.Append(file.Name, parentIndex, isDirectory: false, SearchIndex.ComputeAlias(file.Name));
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
			{
				// Unreadable or vanished directory: skip it and keep scanning siblings
				continue;
			}

			foreach (var (fullName, selfIndex) in subDirectories!)
				pending.Push((fullName, selfIndex));
		}

		// Publish the trailing partial chunk so everything scanned becomes visible
		index.Flush();
	}
}
