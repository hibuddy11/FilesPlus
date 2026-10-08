// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Plugins.QuickSearch;

/// <summary>
/// fzf-style fuzzy matcher (the scoring ideas follow fzf/Lertaro): a query matches a target as a
/// subsequence; the score rewards consecutive runs, word-boundary hits and leading matches, and
/// penalizes gaps. CJK characters match directly, so pinyin matching is done by scoring the
/// pinyin aliases of the name as additional candidate targets.
/// </summary>
internal static class FuzzyMatcher
{
	/// <summary>Returns a score where higher is better, or null when the query does not match.</summary>
	public static int? Score(ReadOnlySpan<char> query, ReadOnlySpan<char> target)
	{
		if (query.IsEmpty)
			return 0;
		if (query.Length > target.Length)
			return null;

		var score = 0;
		var targetIndex = 0;
		var previousMatchIndex = -2;

		for (var i = 0; i < query.Length; i++)
		{
			var queryChar = char.ToLowerInvariant(query[i]);
			var matchedAt = -1;

			for (var j = targetIndex; j < target.Length; j++)
			{
				if (char.ToLowerInvariant(target[j]) != queryChar)
					continue;

				matchedAt = j;
				break;
			}

			if (matchedAt < 0)
				return null;

			// Base point per matched character
			score += 16;

			// Consecutive-run bonus: the previous query char matched right before this one
			if (matchedAt == previousMatchIndex + 1)
				score += 8;

			// Word-boundary bonus: start of the name, after a separator, or next to CJK
			if (matchedAt is 0 || IsBoundaryChar(target[matchedAt - 1]))
				score += 10;

			// Leading match bonus: the first query char also matches the first target char
			if (i == 0 && matchedAt is 0)
				score += 16;

			// Gap penalty for the characters skipped before this match
			var gap = matchedAt - targetIndex;
			if (gap > 0)
				score -= Math.Min(gap, 8);

			previousMatchIndex = matchedAt;
			targetIndex = matchedAt + 1;
		}

		// Prefer shorter targets when the whole query matched
		score -= Math.Min(target.Length / 4, 32);

		return score;
	}

	// Positions right after these are treated as word starts; CJK chars always count as boundaries
	private static bool IsBoundaryChar(char c)
		=> c is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or '+' or '&' or (>= '\u4E00' and <= '\u9FFF');
}
