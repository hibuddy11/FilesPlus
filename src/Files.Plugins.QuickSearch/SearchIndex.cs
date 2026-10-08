// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using TinyPinyin;

namespace Files.Plugins.QuickSearch;

/// <summary>
/// Append-only in-memory index sized for millions of entries. Entries are stored as
/// (name, parent) pairs in fixed-size immutable chunks, so a full relative path is rebuilt by
/// walking the parent chain instead of being duplicated per entry. The scanner thread appends
/// and publishes chunks; readers filter against an atomic chunk snapshot.
/// </summary>
internal sealed class SearchIndex
{
	public const int MaxEntries = 20_000_000;

	private const int ChunkCapacity = 65536;

	internal sealed class Chunk
	{
		public readonly string[] Names = new string[ChunkCapacity];
		public readonly int[] Parents = new int[ChunkCapacity];
		public readonly bool[] Directories = new bool[ChunkCapacity];

		// Null for non-CJK names; otherwise "initials\nfullPinyin" for pinyin matching
		public readonly string?[] Aliases = new string[ChunkCapacity];

		public int BaseIndex;
		public int Count;
	}

	private readonly string rootDirectory;
	private volatile Chunk[] chunks = Array.Empty<Chunk>();
	private Chunk? openChunk;
	private int openCount;
	private int count;
	private int nextBaseIndex;

	public event Action? Published;

	public SearchIndex(string rootDirectory)
	{
		this.rootDirectory = Path.TrimEndingDirectorySeparator(rootDirectory);
	}

	public int Count
		=> Volatile.Read(ref count);

	/// <summary>Immutable chunks published so far; safe to read from any thread.</summary>
	public Chunk[] Snapshot()
		=> chunks;

	/// <summary>
	/// Adds one entry and returns its index, or -1 when the entry cap is reached.
	/// Scanner thread only.
	/// </summary>
	public int Append(string name, int parentIndex, bool isDirectory, string? alias)
	{
		if (count >= MaxEntries)
			return -1;

		var open = openChunk;
		if (open is null)
		{
			open = new Chunk();
			openChunk = open;
			openCount = 0;
		}

		var local = openCount++;
		var index = count++;
		open.Names[local] = name;
		open.Parents[local] = parentIndex;
		open.Directories[local] = isDirectory;
		open.Aliases[local] = alias;

		if (openCount == ChunkCapacity)
			PublishOpenChunk();

		return index;
	}

	/// <summary>Publishes the trailing partial chunk so everything scanned becomes visible. Scanner thread only.</summary>
	public void Flush()
		=> PublishOpenChunk();

	private void PublishOpenChunk()
	{
		var open = openChunk;
		if (open is null)
			return;

		var publishedCount = openCount;
		open.BaseIndex = nextBaseIndex;
		open.Count = publishedCount;
		nextBaseIndex += publishedCount;
		openChunk = null;
		openCount = 0;

		var newChunks = new Chunk[chunks.Length + 1];
		Array.Copy(chunks, newChunks, chunks.Length);
		newChunks[^1] = open;
		chunks = newChunks;

		Published?.Invoke();
	}

	/// <summary>Pinyin aliases for a name containing CJK characters ("initials\nfull"), else null.</summary>
	public static string? ComputeAlias(string name)
	{
		var hasCjk = false;
		foreach (var c in name)
		{
			if (c is >= '\u4E00' and <= '\u9FFF')
			{
				hasCjk = true;
				break;
			}
		}

		if (!hasCjk)
			return null;

		var initials = PinyinHelper.GetPinyinInitials(name);
		var full = PinyinHelper.GetPinyin(name, string.Empty);
		if (string.IsNullOrEmpty(initials) && string.IsNullOrEmpty(full))
			return null;

		return $"{initials}\n{full}";
	}

	public bool IsDirectory(int index)
	{
		var (chunk, local) = Locate(index);
		return chunk.Directories[local];
	}

	public string GetRelativePath(int index)
	{
		var names = new List<string>();
		var current = index;
		while (current >= 0)
		{
			var (chunk, local) = Locate(current);
			names.Add(chunk.Names[local]);
			current = chunk.Parents[local];
		}

		names.Reverse();
		return string.Join("\\", names);
	}

	public string GetFullPath(int index)
		=> Path.Combine(rootDirectory, GetRelativePath(index));

	private (Chunk Chunk, int Local) Locate(int index)
	{
		// Find the last chunk whose base does not exceed the index
		var snapshot = chunks;
		var low = 0;
		var high = snapshot.Length - 1;
		while (low < high)
		{
			var mid = (low + high + 1) / 2;
			if (snapshot[mid].BaseIndex <= index)
				low = mid;
			else
				high = mid - 1;
		}

		var chunk = snapshot[low];
		return (chunk, index - chunk.BaseIndex);
	}
}
