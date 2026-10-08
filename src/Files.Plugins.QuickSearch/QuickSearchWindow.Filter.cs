// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins.QuickSearch;

internal sealed partial class QuickSearchWindow
{
	/// <summary>Flat filter over the immutable chunk snapshot; scores name and pinyin aliases.</summary>
	private static MatchResult Filter(SearchIndex.Chunk[] snapshot, string query)
	{
		if (query.Length is 0)
		{
			// Empty query: list the first indexed entries in scan order
			var first = new List<int>(MaxResults);
			var remaining = MaxResults;
			foreach (var chunk in snapshot)
			{
				if (remaining <= 0)
					break;

				var take = Math.Min(chunk.Count, remaining);
				for (var local = 0; local < take; local++)
					first.Add(chunk.BaseIndex + local);
				remaining -= take;
			}
			return new MatchResult([.. first], first.Count);
		}

		var querySpan = query.AsSpan();
		var matched = new List<int>(capacity: 4096);
		var scores = new List<int>(capacity: 4096);

		foreach (var chunk in snapshot)
		{
			var names = chunk.Names;
			for (var local = 0; local < chunk.Count; local++)
			{
				var name = names[local];
				var score = FuzzyMatcher.Score(querySpan, name);
				if (score is null)
				{
					var alias = chunk.Aliases[local];
					if (alias is null)
						continue;

					// Alias layout: "initials\nfullPinyin"; pinyin matches rank below name matches
					var separator = alias.IndexOf('\n');
					if (FuzzyMatcher.Score(querySpan, alias.AsSpan(0, separator)) is null
						&& FuzzyMatcher.Score(querySpan, alias.AsSpan(separator + 1)) is null)
						continue;

					score = -8;
				}

				// Shorter names rank higher on equal match quality
				matched.Add(chunk.BaseIndex + local);
				scores.Add(score.Value - Math.Min(name.Length / 4, 32));
			}
		}

		// Keep only the best MaxResults via an insertion-sorted top list
		var top = new List<int>(MaxResults + 1);
		var topScores = new List<int>(MaxResults + 1);
		for (var i = 0; i < matched.Count; i++)
		{
			var position = top.Count;
			while (position > 0 && topScores[position - 1] < scores[i])
				position--;

			if (position >= MaxResults)
				continue;

			top.Insert(position, matched[i]);
			topScores.Insert(position, scores[i]);
			if (top.Count > MaxResults)
			{
				top.RemoveAt(top.Count - 1);
				topScores.RemoveAt(topScores.Count - 1);
			}
		}

		return new MatchResult([.. top], matched.Count);
	}

	/// <summary>Relative path for the result list, with a folder marker for directories.</summary>
	private string DisplayEntry(int entryIndex)
	{
		var relative = index.GetRelativePath(entryIndex);
		return index.IsDirectory(entryIndex) ? $"〔文件夹〕 {relative}" : relative;
	}
}
