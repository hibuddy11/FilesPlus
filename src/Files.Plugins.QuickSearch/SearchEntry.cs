// Copyright (c) Files Community
// Licensed under the MIT License.

using TinyPinyin;

namespace Files.Plugins.QuickSearch;

/// <summary>
/// One indexed file-system entry. Pinyin aliases (initials and full pinyin) are computed lazily
/// and only for names containing CJK characters, Lertaro-style.
/// </summary>
internal sealed class SearchEntry
{
	public required string Name { get; init; }
	public required string FullPath { get; init; }
	public required string RelativePath { get; init; }
	public bool IsDirectory { get; init; }

	private string? initials;
	private string? fullPinyin;
	private bool? hasChinese;

	public bool HasChinese
		=> hasChinese ??= Name.Any(PinyinHelper.IsChinese);

	/// <summary>Pinyin initials, e.g. "项目文档" → "XMDW".</summary>
	public string Initials
		=> initials ??= PinyinHelper.GetPinyinInitials(Name);

	/// <summary>Full pinyin without tones or separators, e.g. "项目文档" → "XIANGMUWENDANG".</summary>
	public string FullPinyin
		=> fullPinyin ??= PinyinHelper.GetPinyin(Name, string.Empty);

	/// <summary>Best fuzzy score against the raw name and its pinyin aliases; null when nothing matches.</summary>
	public int? Score(string query)
	{
		var best = FuzzyMatcher.Score(query, Name);

		if (HasChinese)
		{
			// Pinyin alias matches rank just below direct name matches
			if (FuzzyMatcher.Score(query, Initials) is { } byInitials)
				best = Math.Max(best ?? int.MinValue, byInitials - 1);
			if (FuzzyMatcher.Score(query, FullPinyin) is { } byFullPinyin)
				best = Math.Max(best ?? int.MinValue, byFullPinyin - 2);
		}

		return best;
	}

	public override string ToString()
		=> IsDirectory ? $"〔文件夹〕 {RelativePath}" : RelativePath;
}
