// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using TinyPinyin;

namespace Files.Plugins.QuickSearch;

/// <summary>
/// Quick-search plugin: adds a context-menu entry that opens a fzf/pinyin quick-search window
/// over the current folder (search core modeled after Lertaro: in-memory index, fuzzy subsequence
/// matching, pinyin initials and full pinyin).
/// </summary>
public sealed class QuickSearchPlugin : IFilesPlugin, IContextMenuContributor
{
	private IFilesPluginHost host = null!;

	public string Id => "files.quicksearch";
	public string Name => "Quick Search";
	public string Description => "快速搜索当前文件夹（模糊匹配 + 拼音首字母/全拼）";
	public string Version => "1.0";

	public Task InitializeAsync(IFilesPluginHost host, CancellationToken cancellationToken)
	{
		this.host = host;

		// Warm up the pinyin library here so a load failure surfaces (and is logged) during
		// initialization instead of crashing on the first keystroke.
		try
		{
			_ = PinyinHelper.IsChinese('测');
		}
		catch (Exception ex)
		{
			host.LogError(Id, "The pinyin library failed to load; pinyin matching is disabled.", ex);
		}

		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
		=> Task.CompletedTask;

	public IReadOnlyList<PluginContextMenuItem> GetMenuItems(PluginContextMenuContext context)
	{
		if (context.WorkingDirectory is not { Length: > 0 } directory || !Directory.Exists(directory))
			return [];

		return
		[
			new PluginContextMenuItem()
			{
				Text = "快速搜索",
				Glyph = "\uE721",
				Execute = () =>
				{
					try
					{
						new QuickSearchWindow(host, directory).Show();
					}
					catch (Exception ex)
					{
						host.LogError(Id, "Failed to open the quick search window.", ex);
					}

					return Task.CompletedTask;
				},
			},
		];
	}
}
