// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Files.Plugins.Todo;

/// <summary>
/// The Todo tool window. Built in code (no XAML compilation in plugin projects): quick capture
/// box, pending list, done list, screenshot paste/drop, attachment thumbnails.
/// </summary>
internal sealed class TodoWindow : Window
{
	private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

	// WinUI has no ToolTipText property; ToolTipService is the attached equivalent.
	private static void Tip(FrameworkElement element, string text)
		=> ToolTipService.SetToolTip(element, text);

	private readonly TodoStore store;
	private readonly IFilesPluginHost host;
	private readonly string pluginId;

	private readonly TextBox inputBox;
	private readonly TextBlock countText;
	private readonly TextBlock doneHeaderText;
	private readonly StackPanel pendingPanel;
	private readonly StackPanel donePanel;
	private readonly TextBlock statusText;
	private readonly TextBlock directoryText;

	// The task last touched in the list; paste/drop attach to it when pending, otherwise a new task is created.
	private TodoTask? selectedTask;

	public TodoWindow(TodoStore store, IFilesPluginHost host, string pluginId)
	{
		this.store = store;
		this.host = host;
		this.pluginId = pluginId;

		Title = "待办";
		AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 680));

		countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
		inputBox = BuildInputBox();
		doneHeaderText = new TextBlock();
		pendingPanel = new StackPanel();
		donePanel = new StackPanel();
		statusText = new TextBlock
		{
			Opacity = 0.6,
			TextWrapping = TextWrapping.Wrap,
			Text = "回车保存 · Ctrl+V 粘贴截图 · 拖入图片附加到选中任务 · 双击文字编辑",
		};
		directoryText = new TextBlock { Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };

		Content = BuildRoot();

		UpdateDirectoryText();
		_ = ReloadAsync();
	}

	private Grid BuildRoot()
	{
		var pasteButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE724", FontSize = 14 }, // Paste
			VerticalAlignment = VerticalAlignment.Stretch,
		};
		Tip(pasteButton, "粘贴剪贴板截图（附加到选中任务，未选中则新建）");
		pasteButton.Click += async (_, _) => await PasteImageAsync();

		var refreshButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 }, // Refresh
		};
		Tip(refreshButton, "重新读取 todo.txt（外部修改后同步）");
		refreshButton.Click += async (_, _) => await ReloadAsync();

		var changeDirButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE838", FontSize = 14 }, // Folder open
		};
		Tip(changeDirButton, "更改数据目录（todo.txt 所在位置）");
		changeDirButton.Click += async (_, _) => await ChangeDataDirectoryAsync();

		var header = new Grid
		{
			ColumnSpacing = 8,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		header.Children.Add(new TextBlock
		{
			Text = "待办",
			FontSize = 18,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
		});
		Grid.SetColumn(countText, 1);
		header.Children.Add(countText);

		var inputGrid = new Grid
		{
			ColumnSpacing = 6,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		inputGrid.Children.Add(inputBox);
		Grid.SetColumn(pasteButton, 1);
		inputGrid.Children.Add(pasteButton);

		directoryText.PointerPressed += async (_, _) => host.OpenPath(store.DataDirectory);
		Tip(directoryText, $"{store.DataDirectory}（点击打开目录）");

		var footer = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
			ColumnSpacing = 8,
		};
		var directoryStack = new StackPanel { Spacing = 2, Orientation = Orientation.Vertical };
		directoryStack.Children.Add(directoryText);
		directoryStack.Children.Add(statusText);
		Grid.SetColumn(directoryStack, 0);
		footer.Children.Add(directoryStack);
		var footerButtons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 4,
			VerticalAlignment = VerticalAlignment.Center,
		};
		footerButtons.Children.Add(refreshButton);
		footerButtons.Children.Add(changeDirButton);
		Grid.SetColumn(footerButtons, 1);
		footer.Children.Add(footerButtons);

		var root = new Grid
		{
			Margin = new Thickness(12),
			RowSpacing = 10,
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
			},
		};
		root.AllowDrop = true;
		root.DragOver += OnRootDragOver;
		root.Drop += OnRootDrop;

		root.Children.Add(header);
		Grid.SetRow(inputGrid, 1);
		root.Children.Add(inputGrid);

		var pendingScroll = new ScrollViewer { Content = pendingPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		Grid.SetRow(pendingScroll, 2);
		root.Children.Add(pendingScroll);

		var doneScroll = new ScrollViewer
		{
			Content = donePanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			MaxHeight = 200,
		};
		var doneExpander = new Expander
		{
			Header = doneHeaderText,
			Content = doneScroll,
			IsExpanded = false,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
		};
		Grid.SetRow(doneExpander, 3);
		root.Children.Add(doneExpander);

		var separator = new Border
		{
			Height = 1,
			Margin = new Thickness(0, 4, 0, 0),
			BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
			BorderThickness = new Thickness(0, 1, 0, 0),
		};
		Grid.SetRow(separator, 4);
		root.Children.Add(separator);
		Grid.SetRow(footer, 4);
		root.Children.Add(footer);

		return root;
	}

	private TextBox BuildInputBox()
	{
		var box = new TextBox
		{
			PlaceholderText = "记一条待办，回车保存…",
		};
		box.KeyDown += async (_, args) =>
		{
			if (args.Key is VirtualKey.Enter or VirtualKey.GamepadA && !string.IsNullOrWhiteSpace(box.Text))
			{
				await store.AddAsync(box.Text);
				box.Text = string.Empty;
				await ReloadAsync();
			}
		};
		return box;
	}

	public async Task ReloadAsync()
	{
		try
		{
			await store.ReloadAsync();
		}
		catch (Exception ex)
		{
			SetStatus($"读取失败：{ex.Message}");
			return;
		}

		ReloadUI();
	}

	private void ReloadUI()
	{
		pendingPanel.Children.Clear();
		donePanel.Children.Clear();

		foreach (var task in store.Pending)
			pendingPanel.Children.Add(CreateTaskRow(task));

		foreach (var task in store.Done)
			donePanel.Children.Add(CreateTaskRow(task));

		countText.Text = $"{store.Pending.Count} 条待办";
		doneHeaderText.Text = $"已完成 ({store.Done.Count})";
		UpdateDirectoryText();

		if (selectedTask is not null && !store.Pending.Contains(selectedTask))
			selectedTask = null;
	}

	private void UpdateDirectoryText()
	{
		directoryText.Text = store.DataDirectory.Length > 0 ? $"数据：{store.DataDirectory}" : string.Empty;
		Tip(directoryText, $"{store.DataDirectory}（点击打开目录）");
	}

	private Border CreateTaskRow(TodoTask task)
	{
		var checkBox = new CheckBox
		{
			IsChecked = task.IsCompleted,
			VerticalAlignment = VerticalAlignment.Top,
			MinWidth = 0,
		};
		checkBox.Checked += async (_, _) => await ToggleAsync(task, complete: true);
		checkBox.Unchecked += async (_, _) => await ToggleAsync(task, complete: false);

		var bodyBlock = new TextBlock
		{
			Text = task.Body,
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center,
		};
		if (task.IsCompleted)
			bodyBlock.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
		bodyBlock.DoubleTapped += (_, _) => StartEdit(task, bodyBlock);

		var priorityButton = new Button
		{
			Content = task.Priority is { } p ? $"({p})" : "\uE7C9", // Flag or circle
			FontSize = 12,
			Padding = new Thickness(4, 2, 4, 2),
			MinWidth = 0,
			MinHeight = 0,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Tip(priorityButton, "优先级（点击选择）");
		priorityButton.Flyout = BuildPriorityMenu(task);

		var bodyLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
		bodyLine.Children.Add(priorityButton);
		bodyLine.Children.Add(bodyBlock);

		var deleteButton = new Button
		{
			Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 }, // Delete
			Padding = new Thickness(4, 2, 4, 2),
			MinWidth = 0,
			MinHeight = 0,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Tip(deleteButton, "删除任务");
		deleteButton.Click += async (_, _) =>
		{
			await store.DeleteAsync(task);
			await ReloadAsync();
		};

		var row = new Grid
		{
			ColumnSpacing = 6,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = GridLength.Auto },
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = GridLength.Auto },
			},
		};
		row.Children.Add(checkBox);
		var center = new StackPanel { Spacing = 4 };
		center.Children.Add(bodyLine);
		if (task.Attachments.Count > 0)
			center.Children.Add(BuildAttachmentStrip(task));
		Grid.SetColumn(center, 1);
		row.Children.Add(center);
		Grid.SetColumn(deleteButton, 2);
		row.Children.Add(deleteButton);

		var border = new Border
		{
			Child = row,
			Padding = new Thickness(6, 4, 6, 4),
			CornerRadius = new CornerRadius(4),
		};
		border.PointerPressed += (_, _) => selectedTask = task;
		if (ReferenceEquals(selectedTask, task))
			border.Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"];

		return border;
	}

	private MenuFlyout BuildPriorityMenu(TodoTask task)
	{
		var menu = new MenuFlyout();
		foreach (var priority in new char?[] { 'A', 'B', 'C', null })
		{
			var item = new MenuFlyoutItem { Text = priority is { } p ? $"优先级 {p}" : "清除优先级" };
			item.Click += async (_, _) =>
			{
				await store.SetPriorityAsync(task, priority);
				await ReloadAsync();
			};
			menu.Items.Add(item);
		}
		return menu;
	}

	private StackPanel BuildAttachmentStrip(TodoTask task)
	{
		var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
		foreach (var attachment in task.Attachments)
			strip.Children.Add(CreateThumb(task, attachment));
		return strip;
	}

	private Button CreateThumb(TodoTask task, TodoAttachment attachment)
	{
		var image = new Image
		{
			Width = 44,
			Height = 44,
			Stretch = Stretch.UniformToFill,
		};
		if (attachment.FullPath is { } path && File.Exists(path))
		{
			var bitmap = new BitmapImage { DecodePixelWidth = 96, UriSource = new Uri(path) };
			image.Source = bitmap;
		}

		var thumb = new Button
		{
			Content = image,
			Padding = new Thickness(1),
			MinWidth = 0,
			MinHeight = 0,
			BorderThickness = new Thickness(0),
			Background = null,
		};
		Tip(thumb, attachment.FileName);
		thumb.Click += (_, _) =>
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
		remove.Click += async (_, _) =>
		{
			await store.RemoveAttachmentAsync(task, attachment);
			await ReloadAsync();
		};
		thumb.ContextFlyout = new MenuFlyout { Items = { openFolder, remove } };

		return thumb;
	}

	private void StartEdit(TodoTask task, TextBlock bodyBlock)
	{
		if (bodyBlock.Parent is not StackPanel line)
			return;

		var index = line.Children.IndexOf(bodyBlock);
		var editBox = new TextBox { Text = task.Body };
		TextBox? committed = null;

		async void Commit()
		{
			if (committed is null)
				return;
			committed = null;

			var newText = editBox.Text;
			line.Children.RemoveAt(index);
			line.Children.Insert(index, bodyBlock);

			if (newText != task.Body && !string.IsNullOrWhiteSpace(newText))
			{
				await store.UpdateBodyAsync(task, newText);
				await ReloadAsync();
			}
		}

		editBox.KeyDown += (_, args) =>
		{
			if (args.Key is VirtualKey.Enter)
				Commit();
			else if (args.Key is VirtualKey.Escape)
				committed = null;
		};
		editBox.LostFocus += (_, _) => Commit();

		committed = editBox;
		line.Children.RemoveAt(index);
		line.Children.Insert(index, editBox);
		editBox.Focus(FocusState.Programmatic);
		editBox.SelectAll();
	}

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

	private void SetStatus(string message) => statusText.Text = message;
}
