// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Files.Plugins.Todo;

/// <summary>
/// The Todo tool window. Built in code (no XAML compilation in plugin projects). Card-style task
/// rows, colored priority pills, screenshot paste/drop, thumbnail attachments, done expander.
/// </summary>
internal sealed class TodoWindow : Window
{
	private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

	private readonly TodoStore store;
	private readonly IFilesPluginHost host;
	private readonly string pluginId;

	private readonly TextBox inputBox;
	private readonly Border countPill;
	private readonly TextBlock countText = new();
	private readonly StackPanel pendingPanel;
	private readonly StackPanel emptyState;
	private readonly Expander doneExpander;
	private readonly TextBlock doneHeaderText;
	private readonly StackPanel donePanel;
	private readonly TextBlock statusText;
	private readonly TextBlock directoryText;

	private TodoTask? selectedTask;
	private bool wasActivated;

	public TodoWindow(TodoStore store, IFilesPluginHost host, string pluginId)
	{
		this.store = store;
		this.host = host;
		this.pluginId = pluginId;

		Title = "待办";
		AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 780));
		SystemBackdrop = new MicaBackdrop();

		inputBox = BuildInputBox();
		countPill = BuildCountPill();
		pendingPanel = new StackPanel();
		donePanel = new StackPanel();
		emptyState = BuildEmptyState();
		doneHeaderText = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
		doneExpander = new Expander
		{
			Header = doneHeaderText,
			IsExpanded = false,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
		};
		// The content tree is built exactly once; ReloadUI only clears/fills the shared panels.
		// Replacing Expander.Content while a Click event is dispatched from it crashes WinUI (0x80070057).
		doneExpander.Content = BuildDoneContent();
		statusText = new TextBlock
		{
			Opacity = 0.65,
			FontSize = 12,
			TextWrapping = TextWrapping.Wrap,
			Text = "回车保存 · Ctrl+V 粘贴截图 · 拖入图片附加 · 双击文字编辑",
		};
		directoryText = new TextBlock
		{
			Opacity = 0.65,
			FontSize = 12,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		directoryText.PointerPressed += (_, _) => host.OpenPath(store.DataDirectory);

		Content = BuildRoot();
		Activated += (_, _) =>
		{
			if (wasActivated)
				return;
			wasActivated = true;
			inputBox.Focus(FocusState.Programmatic);
		};

		UpdateDirectoryText();
		_ = ReloadAsync();
	}

	private static void Tip(FrameworkElement element, string text)
		=> ToolTipService.SetToolTip(element, text);

	private static Brush Res(string key)
		=> (Brush)Application.Current.Resources[key];

	private static Color FromHex(string hex)
		=> Color.FromArgb(
			byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
			byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
			byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
			byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));

	private static (Color bg, Color fg) PriorityStyle(char priority) => priority switch
	{
		'A' => (FromHex("FFE23B3B"), Color.FromArgb(255, 255, 255, 255)),
		'B' => (FromHex("FFFFB900"), Color.FromArgb(255, 0, 0, 0)),
		'C' => (FromHex("FF0078D7"), Color.FromArgb(255, 255, 255, 255)),
		_ => (FromHex("FF8A8888"), Color.FromArgb(255, 255, 255, 255)),
	};

	// --- Layout ---

	private Grid BuildRoot()
	{
		var pasteAccelerator = new KeyboardAccelerator
		{
			Key = VirtualKey.V,
			Modifiers = VirtualKeyModifiers.Control,
		};
		pasteAccelerator.Invoked += (_, args) =>
		{
			if (!ClipboardHasImage())
				return;
			args.Handled = true;
			RunSafe(PasteImageAsync);
		};

		var root = new Grid
		{
			Margin = new Thickness(18, 14, 18, 14),
			RowSpacing = 12,
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
			},
		};
		root.KeyboardAccelerators.Add(pasteAccelerator);
		root.AllowDrop = true;
		root.DragOver += OnRootDragOver;
		root.Drop += OnRootDrop;

		root.Children.Add(BuildHeader());

		var inputRow = BuildInputRow();
		Grid.SetRow(inputRow, 1);
		root.Children.Add(inputRow);

		var pendingScroll = new ScrollViewer
		{
			Content = pendingPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			HorizontalScrollMode = ScrollMode.Disabled,
		};
		var listGrid = new Grid();
		listGrid.Children.Add(pendingScroll);
		listGrid.Children.Add(emptyState);
		Grid.SetRow(listGrid, 2);
		root.Children.Add(listGrid);

		Grid.SetRow(doneExpander, 3);
		root.Children.Add(doneExpander);

		var footer = BuildFooter();
		Grid.SetRow(footer, 4);
		root.Children.Add(footer);

		return root;
	}

	private Grid BuildHeader()
	{
		var accent = (Color)Application.Current.Resources["SystemAccentColor"];

		var iconTile = new Border
		{
			Width = 34,
			Height = 34,
			CornerRadius = new CornerRadius(8),
			Background = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new FontIcon
			{
				Glyph = "\uE73E", // CheckMark
				FontSize = 17,
				Foreground = new SolidColorBrush(accent),
			},
		};

		var title = new TextBlock
		{
			Text = "待办",
			FontSize = 19,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};

		var left = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10,
			VerticalAlignment = VerticalAlignment.Center,
		};
		left.Children.Add(iconTile);
		left.Children.Add(title);

		var header = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		header.Children.Add(left);
		Grid.SetColumn(countPill, 1);
		header.Children.Add(countPill);

		return header;
	}

	private Border BuildCountPill()
	{
		var accent = (Color)Application.Current.Resources["SystemAccentColor"];

		countText.FontSize = 12;
		countText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
		countText.Foreground = new SolidColorBrush(accent);

		return new Border
		{
			CornerRadius = new CornerRadius(10),
			Padding = new Thickness(10, 3, 10, 3),
			Background = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
			VerticalAlignment = VerticalAlignment.Center,
			Child = countText,
		};
	}

	private Grid BuildInputRow()
	{
		var pasteButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE77F", FontSize = 15 }, // Paste
			VerticalAlignment = VerticalAlignment.Stretch,
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(10, 0, 10, 0),
		};
		Tip(pasteButton, "粘贴剪贴板截图（附加到选中任务，未选中则新建）");
		pasteButton.Click += (_, _) => RunSafe(PasteImageAsync);

		var row = new Grid
		{
			ColumnSpacing = 8,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		row.Children.Add(inputBox);
		Grid.SetColumn(pasteButton, 1);
		row.Children.Add(pasteButton);

		return row;
	}

	private TextBox BuildInputBox()
	{
		var box = new TextBox
		{
			PlaceholderText = "记一条待办，回车保存…",
			CornerRadius = new CornerRadius(8),
			FontSize = 14,
		};
		box.KeyDown += (_, args) =>
		{
			if (args.Key is VirtualKey.Enter && !string.IsNullOrWhiteSpace(box.Text))
				RunSafe(async () =>
				{
					var text = box.Text;
					await store.AddAsync(text);
					box.Text = string.Empty;
					await ReloadAsync();
				});
		};
		return box;
	}

	private StackPanel BuildEmptyState()
	{
		var accent = (Color)Application.Current.Resources["SystemAccentColor"];

		var panel = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			Margin = new Thickness(0, 40, 0, 40),
		};
		panel.Children.Add(new FontIcon
		{
			Glyph = "\uE73E", // CheckMark
			FontSize = 42,
			Foreground = new SolidColorBrush(Color.FromArgb(0x88, accent.R, accent.G, accent.B)),
		});
		panel.Children.Add(new TextBlock
		{
			Text = "没有待办事项",
			FontSize = 15,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 10, 0, 0),
		});
		panel.Children.Add(new TextBlock
		{
			Text = "工作中想到什么，随手记到这里",
			FontSize = 12,
			Opacity = 0.6,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 4, 0, 0),
		});
		return panel;
	}

	private StackPanel BuildFooter()
	{
		var refreshButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 }, // Refresh
			Padding = new Thickness(8, 5, 8, 5),
			MinWidth = 0,
		};
		Tip(refreshButton, "重新读取 todo.txt（外部修改后同步）");
		refreshButton.Click += (_, _) => RunSafe(ReloadAsync);

		var changeDirButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE838", FontSize = 14 }, // FolderOpen
			Padding = new Thickness(8, 5, 8, 5),
			MinWidth = 0,
		};
		Tip(changeDirButton, "更改数据目录（todo.txt 所在位置）");
		changeDirButton.Click += (_, _) => RunSafe(ChangeDataDirectoryAsync);

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 4,
			VerticalAlignment = VerticalAlignment.Bottom,
		};
		buttons.Children.Add(refreshButton);
		buttons.Children.Add(changeDirButton);

		var info = new StackPanel { Spacing = 3 };
		info.Children.Add(directoryText);
		info.Children.Add(statusText);

		var content = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
			ColumnSpacing = 8,
		};
		content.Children.Add(info);
		Grid.SetColumn(buttons, 1);
		content.Children.Add(buttons);

		var footer = new StackPanel();
		footer.Children.Add(new Border
		{
			Height = 1,
			Margin = new Thickness(0, 0, 0, 8),
			BorderBrush = Res("ControlStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(0, 1, 0, 0),
		});
		footer.Children.Add(content);
		return footer;
	}

	// --- Task rows ---

	public async Task ReloadAsync()
	{
		try
		{
			await store.ReloadAsync();
			ReloadUI();
		}
		catch (Exception ex)
		{
			SetStatus($"读取失败：{ex.Message}");
		}
	}

	// Async event handlers must never let exceptions escape to the dispatcher: that tears the process down.
	// Fire-and-forget by design: the whole body is guarded and failures surface on the status line.
	private async void RunSafe(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			SetStatus($"操作失败：{ex.Message}");
		}
	}

	private void ReloadUI()
	{
		pendingPanel.Children.Clear();
		donePanel.Children.Clear();

		foreach (var task in store.Pending)
			pendingPanel.Children.Add(CreateTaskRow(task));
		foreach (var task in store.Done)
			donePanel.Children.Add(CreateTaskRow(task));

		countText.Text = store.Pending.Count > 0 ? $"{store.Pending.Count} 项待办" : "0";
		emptyState.Visibility = store.Pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		doneHeaderText.Text = $"已完成 ({store.Done.Count})";
		doneExpander.Visibility = store.Done.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		UpdateDirectoryText();

		if (selectedTask is not null && !store.Pending.Contains(selectedTask) && !store.Done.Contains(selectedTask))
			selectedTask = null;
	}

	private StackPanel BuildDoneContent()
	{
		var panel = new StackPanel();

		var scroll = new ScrollViewer
		{
			Content = donePanel,
			MaxHeight = 240,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			HorizontalScrollMode = ScrollMode.Disabled,
		};
		panel.Children.Add(scroll);

		var clearButton = new HyperlinkButton
		{
			Content = "清空已完成",
			FontSize = 12,
			Padding = new Thickness(0, 6, 0, 0),
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		clearButton.Click += (_, _) => RunSafe(async () =>
		{
			await store.ClearDoneAsync();
			await ReloadAsync();
		});
		panel.Children.Add(clearButton);

		return panel;
	}

	private Border CreateTaskRow(TodoTask task)
	{
		var card = new Border
		{
			Tag = task,
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(10, 8, 6, 8),
			Margin = new Thickness(0, 0, 0, 6),
			Background = Res("LayerFillColorDefaultBrush"),
			BorderBrush = Res("ControlStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1),
		};
		if (task.IsCompleted)
			card.Opacity = 0.62;
		if (ReferenceEquals(selectedTask, task))
			card.Background = Res("SubtleFillColorSecondaryBrush");

		card.PointerEntered += (_, _) =>
		{
			if (!ReferenceEquals(selectedTask, task))
				card.Background = Res("SubtleFillColorSecondaryBrush");
		};
		card.PointerExited += (_, _) =>
		{
			if (!ReferenceEquals(selectedTask, task))
				card.Background = Res("LayerFillColorDefaultBrush");
		};
		card.PointerPressed += (_, _) =>
		{
			selectedTask = task;
			UpdateCardSelections();
		};

		var checkBox = new CheckBox
		{
			IsChecked = task.IsCompleted,
			VerticalAlignment = VerticalAlignment.Top,
			MinWidth = 0,
			Margin = new Thickness(0, 2, 0, 0),
		};
		checkBox.Checked += (_, _) => RunSafe(() => ToggleAsync(task, complete: true));
		checkBox.Unchecked += (_, _) => RunSafe(() => ToggleAsync(task, complete: false));

		var bodyBlock = new TextBlock
		{
			Text = task.Body,
			TextWrapping = TextWrapping.Wrap,
			FontSize = 14,
			VerticalAlignment = VerticalAlignment.Center,
		};
		if (task.IsCompleted)
			bodyBlock.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
		bodyBlock.DoubleTapped += (_, _) => StartEdit(task, bodyBlock);

		var bodyLine = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
		};
		bodyLine.Children.Add(BuildPriorityChip(task));
		bodyLine.Children.Add(bodyBlock);

		var content = new StackPanel { Spacing = 6 };
		content.Children.Add(bodyLine);
		if (!string.IsNullOrEmpty(task.Created))
		{
			content.Children.Add(new TextBlock
			{
				Text = task.Created.Length >= 10 ? task.Created[5..] : task.Created,
				FontSize = 11,
				Opacity = 0.55,
				Margin = new Thickness(30, 0, 0, 0),
			});
		}
		if (task.Attachments.Count > 0)
			content.Children.Add(BuildAttachmentStrip(task));

		var deleteButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }, // Delete
			Padding = new Thickness(6, 4, 6, 4),
			MinWidth = 0,
			VerticalAlignment = VerticalAlignment.Top,
			Background = null,
			BorderThickness = new Thickness(0),
		};
		Tip(deleteButton, "删除任务");
		deleteButton.Click += (_, _) => RunSafe(async () =>
		{
			await store.DeleteAsync(task);
			await ReloadAsync();
		});

		var grid = new Grid
		{
			ColumnSpacing = 10,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = GridLength.Auto },
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		grid.Children.Add(checkBox);
		Grid.SetColumn(content, 1);
		grid.Children.Add(content);
		Grid.SetColumn(deleteButton, 2);
		grid.Children.Add(deleteButton);

		card.Child = grid;
		return card;
	}

	private void UpdateDirectoryText()
	{
		directoryText.Text = store.DataDirectory.Length > 0 ? $"数据：{store.DataDirectory}" : string.Empty;
		Tip(directoryText, $"{store.DataDirectory}（点击打开目录）");
	}

	private void UpdateCardSelections()
	{
		foreach (var card in pendingPanel.Children.Concat(donePanel.Children).OfType<Border>())
		{
			card.Background = card.Tag is TodoTask task && ReferenceEquals(task, selectedTask)
				? Res("SubtleFillColorSecondaryBrush")
				: Res("LayerFillColorDefaultBrush");
		}
	}

	private Border BuildPriorityChip(TodoTask task)
	{
		Border chip;
		if (task.Priority is { } priority)
		{
			var (bg, fg) = PriorityStyle(priority);
			chip = new Border
			{
				Width = 22,
				Height = 22,
				CornerRadius = new CornerRadius(11),
				Background = new SolidColorBrush(bg),
				VerticalAlignment = VerticalAlignment.Center,
				Child = new TextBlock
				{
					Text = priority.ToString(),
					FontSize = 11,
					FontWeight = Microsoft.UI.Text.FontWeights.Bold,
					Foreground = new SolidColorBrush(fg),
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
				},
			};
		}
		else
		{
			chip = new Border
			{
				Width = 22,
				Height = 22,
				CornerRadius = new CornerRadius(11),
				Background = Res("SubtleFillColorSecondaryBrush"),
				VerticalAlignment = VerticalAlignment.Center,
				Child = new FontIcon
				{
					Glyph = "\uE7C1", // Flag
					FontSize = 11,
					Foreground = Res("TextFillColorSecondaryBrush"),
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
				},
			};
		}

		var flyout = new MenuFlyout();
		foreach (var level in new char?[] { 'A', 'B', 'C', null })
		{
			var item = new MenuFlyoutItem
			{
				Text = level is { } p ? $"优先级 {p}" : "清除优先级",
			};
			item.Click += (_, _) => RunSafe(async () =>
			{
				await store.SetPriorityAsync(task, level);
				await ReloadAsync();
			});
			flyout.Items.Add(item);
		}

		chip.Tapped += (_, e) =>
		{
			flyout.ShowAt(chip, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
			e.Handled = true;
		};
		Tip(chip, "优先级（点击设置）");

		return chip;
	}

	private StackPanel BuildAttachmentStrip(TodoTask task)
	{
		var strip = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			Margin = new Thickness(30, 0, 0, 0),
		};
		foreach (var attachment in task.Attachments)
			strip.Children.Add(CreateThumb(task, attachment));
		return strip;
	}

	private Border CreateThumb(TodoTask task, TodoAttachment attachment)
	{
		var thumb = new Border
		{
			Width = 52,
			Height = 52,
			CornerRadius = new CornerRadius(6),
			BorderBrush = Res("ControlStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1),
			HorizontalAlignment = HorizontalAlignment.Left,
		};

		if (attachment.FullPath is { } path && File.Exists(path))
		{
			thumb.Background = new ImageBrush
			{
				ImageSource = new BitmapImage { DecodePixelWidth = 120, UriSource = new Uri(path) },
			};
		}
		else
		{
			thumb.Background = Res("SubtleFillColorTertiaryBrush");
			thumb.Child = new FontIcon
			{
				Glyph = "\uEB9F", // Photo
				FontSize = 18,
				Foreground = Res("TextFillColorSecondaryBrush"),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
			};
		}

		Tip(thumb, attachment.FileName);
		thumb.Tapped += (_, _) =>
		{
			if (attachment.FullPath is { } openPath)
				host.OpenPath(openPath);
		};

		var openFolder = new MenuFlyoutItem { Text = "打开所在文件夹" };
		openFolder.Click += (_, _) =>
		{
			if (attachment.FullPath is { } p)
				host.OpenPath(Path.GetDirectoryName(p) ?? p);
		};
		var remove = new MenuFlyoutItem { Text = "删除附件" };
		remove.Click += (_, _) => RunSafe(async () =>
		{
			await store.RemoveAttachmentAsync(task, attachment);
			await ReloadAsync();
		});
		thumb.ContextFlyout = new MenuFlyout { Items = { openFolder, remove } };

		return thumb;
	}

	private void StartEdit(TodoTask task, TextBlock bodyBlock)
	{
		if (bodyBlock.Parent is not StackPanel line)
			return;

		var index = line.Children.IndexOf(bodyBlock);
		var editBox = new TextBox { Text = task.Body, FontSize = 14 };
		TextBox? pending = null;

		async void Commit()
		{
			if (pending is null)
				return;
			pending = null;

			try
			{
				var newText = editBox.Text;
				line.Children.RemoveAt(index);
				line.Children.Insert(index, bodyBlock);

				if (newText != task.Body && !string.IsNullOrWhiteSpace(newText))
				{
					await store.UpdateBodyAsync(task, newText);
					await ReloadAsync();
				}
			}
			catch (Exception ex)
			{
				SetStatus($"保存失败：{ex.Message}");
			}
		}

		editBox.KeyDown += (_, args) =>
		{
			if (args.Key is VirtualKey.Enter)
				Commit();
			else if (args.Key is VirtualKey.Escape)
				pending = null;
		};
		editBox.LostFocus += (_, _) => Commit();

		pending = editBox;
		line.Children.RemoveAt(index);
		line.Children.Insert(index, editBox);
		editBox.Focus(FocusState.Programmatic);
		editBox.SelectAll();
	}

	// --- Actions ---

	private async Task ToggleAsync(TodoTask task, bool complete)
	{
		if (complete && !task.IsCompleted)
			await store.CompleteAsync(task);
		else if (!complete && task.IsCompleted)
			await store.ReopenAsync(task);

		await ReloadAsync();
	}

	private async Task PasteImageAsync()
	{
		try
		{
			var attachment = await store.AddAttachmentFromClipboardAsync();
			if (attachment is null)
			{
				SetStatus("剪贴板中没有图片（可先 Win+Shift+S 截图）");
				return;
			}

			var text = GetClipboardText();
			if (selectedTask is { IsCompleted: false } target)
			{
				await store.RegisterAttachmentAsync(target, attachment);
				SetStatus($"已附加到选中任务：{attachment.FileName}");
			}
			else
			{
				await store.AddAsync(string.IsNullOrWhiteSpace(text) ? "截图" : text, [attachment]);
				SetStatus($"已新建任务并附加截图：{attachment.FileName}");
			}

			await ReloadAsync();
		}
		catch (Exception ex)
		{
			SetStatus($"粘贴失败：{ex.Message}");
		}
	}

	private static bool ClipboardHasImage()
	{
		try
		{
			return Clipboard.GetContent().Contains(StandardDataFormats.Bitmap);
		}
		catch
		{
			return false;
		}
	}

	private static string? GetClipboardText()
	{
		try
		{
			var content = Clipboard.GetContent();
			if (content.Contains(StandardDataFormats.Text))
				return content.GetTextAsync().AsTask().GetAwaiter().GetResult();
		}
		catch
		{
			// Clipboard access can fail transiently; treat as no text.
		}
		return null;
	}

	private void OnRootDragOver(object sender, DragEventArgs args)
	{
		var isImage = args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Bitmap);
		args.AcceptedOperation = isImage ? DataPackageOperation.Copy : DataPackageOperation.None;
		if (isImage)
		{
			args.DragUIOverride.Caption = "附加到待办";
			args.DragUIOverride.IsCaptionVisible = true;
			args.DragUIOverride.IsGlyphVisible = true;
		}
		args.Handled = true;
	}

	private async void OnRootDrop(object sender, DragEventArgs args)
	{
		args.Handled = true;
		try
		{
			var added = 0;

			if (args.DataView.Contains(StandardDataFormats.StorageItems))
			{
				var items = await args.DataView.GetStorageItemsAsync();
				foreach (var item in items.OfType<StorageFile>())
				{
					if (!ImageExtensions.Contains(Path.GetExtension(item.Path), StringComparer.OrdinalIgnoreCase))
						continue;

					await AttachAsync(item.Path, Path.GetFileNameWithoutExtension(item.Path));
					added++;
				}
			}
			else if (args.DataView.Contains(StandardDataFormats.Bitmap))
			{
				var reference = await args.DataView.GetBitmapAsync();
				var attachment = await store.AddAttachmentFromStreamAsync(reference);
				if (attachment is not null)
				{
					await AttachRegisteredAsync(attachment, "截图");
					added++;
				}
			}

			SetStatus(added > 0 ? $"已附加 {added} 张图片" : "没有可附加的图片文件");
			await ReloadAsync();
		}
		catch (Exception ex)
		{
			SetStatus($"拖放失败：{ex.Message}");
		}
	}

	/// <summary>Copies an image file and attaches it to the selected pending task, or creates a new one.</summary>
	private async Task AttachAsync(string sourcePath, string fallbackName)
	{
		if (selectedTask is { IsCompleted: false } target)
		{
			await store.AddAttachmentFromFileAsync(target, sourcePath);
			SetStatus($"已附加到选中任务：{Path.GetFileName(sourcePath)}");
		}
		else
		{
			var task = await store.AddAsync(fallbackName);
			await store.AddAttachmentFromFileAsync(task, sourcePath);
			SetStatus($"已新建任务并附加：{Path.GetFileName(sourcePath)}");
		}
	}

	/// <summary>Attaches an already-saved attachment to the selected pending task, or creates a new one.</summary>
	private async Task AttachRegisteredAsync(TodoAttachment attachment, string fallbackName)
	{
		if (selectedTask is { IsCompleted: false } target)
		{
			await store.RegisterAttachmentAsync(target, attachment);
			SetStatus($"已附加到选中任务：{attachment.FileName}");
		}
		else
		{
			await store.AddAsync(fallbackName, [attachment]);
			SetStatus($"已新建任务并附加截图：{attachment.FileName}");
		}
	}

	private async Task ChangeDataDirectoryAsync()
	{
		var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

		var folder = await picker.PickSingleFolderAsync();
		if (folder is null)
			return;

		await store.SetDataDirectoryAsync(folder.Path);
		await ReloadAsync();
		SetStatus($"数据目录已更改为：{folder.Path}");
	}

	private void SetStatus(string message)
		=> statusText.Text = message;
}
