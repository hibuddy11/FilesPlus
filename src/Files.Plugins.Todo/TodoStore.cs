// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text;
using System.Text.Json;

namespace Files.Plugins.Todo;

/// <summary>An image (or file) attached to a task, stored under the data directory's attachments folder.</summary>
public sealed class TodoAttachment
{
	public required string FileName { get; init; }

	/// <summary>Null until the owning store resolves the data directory.</summary>
	public string? FullPath { get; set; }
}

/// <summary>
/// A single task. Round-trips the todo.txt format: unknown extensions (due:, +project, @context…)
/// stay inside <see cref="Body"/> so other todo.txt apps keep working; only the att: keys are
/// parsed out and re-emitted.
/// </summary>
public sealed class TodoTask
{
	public bool IsCompleted { get; internal set; }

	/// <summary>Completion date (yyyy-MM-dd), set when the task is completed.</summary>
	public string? CompletedDate { get; internal set; }

	/// <summary>Priority letter (A-Z) without parentheses, or null.</summary>
	public char? Priority { get; internal set; }

	/// <summary>Creation date (yyyy-MM-dd), or null.</summary>
	public string? Created { get; internal set; }

	/// <summary>Task text with priority/dates/att: keys stripped.</summary>
	public string Body { get; internal set; } = string.Empty;

	public List<TodoAttachment> Attachments { get; } = [];

	/// <summary>Original text of a plain line we never modified, so foreign lines are written back verbatim.</summary>
	public string? RawLine { get; internal set; }

	internal string Format()
	{
		if (RawLine is not null && !IsCompleted)
			return RawLine;

		var sb = new StringBuilder();
		if (IsCompleted)
		{
			sb.Append("x ").Append(CompletedDate ?? DateTime.Now.ToString("yyyy-MM-dd")).Append(' ');
		}
		if (Priority is { } priority)
			sb.Append('(').Append(priority).Append(") ");
		if (!string.IsNullOrEmpty(Created))
			sb.Append(Created).Append(' ');
		sb.Append(Body);
		foreach (var attachment in Attachments)
			sb.Append(" att:").Append(attachment.FileName);

		return sb.ToString();
	}

	private static readonly System.Text.RegularExpressions.Regex AttRegex =
		new(@"(?:^|\s)att:(?<file>\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

	/// <summary>Parses one todo.txt line. Never returns null for non-empty input.</summary>
	public static TodoTask Parse(string line)
	{
		var task = new TodoTask();
		var rest = line.Trim();

		if (rest.StartsWith("x ", StringComparison.Ordinal))
		{
			task.IsCompleted = true;
			rest = rest[2..].TrimStart();
			if (rest.Length >= 10 && TryTakeDate(rest[..10]))
			{
				task.CompletedDate = rest[..10];
				rest = rest[10..].TrimStart();
			}
		}

		if (rest.Length >= 3 && rest[0] == '(' && rest[2] == ')' && char.IsAsciiLetterUpper(rest[1]))
		{
			task.Priority = rest[1];
			rest = rest[3..].TrimStart();
		}

		if (rest.Length >= 10 && TryTakeDate(rest[..10]))
		{
			task.Created = rest[..10];
			rest = rest[10..].TrimStart();
		}

		foreach (System.Text.RegularExpressions.Match match in AttRegex.Matches(rest))
			task.Attachments.Add(new TodoAttachment { FileName = match.Groups["file"].Value });
		rest = AttRegex.Replace(rest, " ").Trim();

		task.Body = rest;

		// Plain unmodified line (nothing we would reformat): keep verbatim for round-tripping
		if (!task.IsCompleted && task.Priority is null && task.Created is null && task.Attachments.Count == 0)
			task.RawLine = line;

		return task;
	}

	private static bool TryTakeDate(string candidate)
	{
		return candidate.Length == 10
			&& candidate[4] == '-'
			&& candidate[7] == '-'
			&& DateOnly.TryParseExact(candidate, "yyyy-MM-dd", out _);
	}
}

/// <summary>
/// Loads and saves the task files. Pending tasks live in todo.txt, completed ones in done.txt
/// (standard todo.txt convention). All writes are atomic (temp file + move) and serialized.
/// </summary>
public sealed class TodoStore
{
	private readonly IFilesPluginHost host;
	private readonly string pluginId;
	private readonly SemaphoreSlim gate = new(1, 1);

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public TodoStore(IFilesPluginHost host, string pluginId)
	{
		this.host = host;
		this.pluginId = pluginId;
	}

	public string DataDirectory { get; private set; } = string.Empty;

	private string TodoFilePath => Path.Combine(DataDirectory, "todo.txt");

	private string DoneFilePath => Path.Combine(DataDirectory, "done.txt");

	private string AttachmentsDirectory => Path.Combine(DataDirectory, "attachments");

	private string ConfigFilePath => Path.Combine(host.GetPluginDataDirectory(pluginId), "config.json");

	public List<TodoTask> Pending { get; private set; } = [];

	public List<TodoTask> Done { get; private set; } = [];

	/// <summary>Resolves the data directory (default Documents\Todo), creates it and loads the files.</summary>
	public async Task EnsureReadyAsync()
	{
		DataDirectory = LoadConfiguredDirectory() ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Todo");

		Directory.CreateDirectory(DataDirectory);
		Directory.CreateDirectory(AttachmentsDirectory);
		if (!File.Exists(TodoFilePath))
			await File.WriteAllTextAsync(TodoFilePath, string.Empty, new UTF8Encoding(false));

		await ReloadAsync();
	}

	public async Task SetDataDirectoryAsync(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		Directory.CreateDirectory(Path.Combine(path, "attachments"));

		DataDirectory = path;
		var json = JsonSerializer.Serialize(new { DataDirectory = path }, JsonOptions);
		await File.WriteAllTextAsync(ConfigFilePath, json, new UTF8Encoding(false));

		await ReloadAsync();
	}

	private string? LoadConfiguredDirectory()
	{
		try
		{
			if (!File.Exists(ConfigFilePath))
				return null;

			var doc = JsonDocument.Parse(File.ReadAllText(ConfigFilePath));
			return doc.RootElement.TryGetProperty("DataDirectory", out var value) && value.GetString() is { Length: > 0 } path
				? path
				: null;
		}
		catch (Exception ex)
		{
			host.LogError(pluginId, "Failed to read the Todo data directory config; using the default.", ex);
			return null;
		}
	}

	public async Task ReloadAsync()
	{
		await gate.WaitAsync();
		try
		{
			Pending = [.. (await ReadLinesAsync(TodoFilePath)).Select(TodoTask.Parse)];
			Done = [.. (await ReadLinesAsync(DoneFilePath)).Select(TodoTask.Parse)];
			foreach (var task in Pending.Concat(Done))
			{
				foreach (var attachment in task.Attachments)
					attachment.FullPath = Path.Combine(AttachmentsDirectory, attachment.FileName);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<TodoTask> AddAsync(string body, IEnumerable<TodoAttachment>? attachments = null)
	{
		await gate.WaitAsync();
		try
		{
			var task = new TodoTask
			{
				Created = DateTime.Now.ToString("yyyy-MM-dd"),
				Body = body.Trim(),
			};
			if (attachments is not null)
				task.Attachments.AddRange(attachments);
			FillAttachmentPaths(task);

			Pending.Add(task);
			await WriteAsync(Pending, TodoFilePath);
			return task;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task CompleteAsync(TodoTask task)
	{
		await gate.WaitAsync();
		try
		{
			Pending.Remove(task);
			task.IsCompleted = true;
			task.CompletedDate = DateTime.Now.ToString("yyyy-MM-dd");
			task.RawLine = null;
			Done.Add(task);
			await WriteBothAsync();
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task ReopenAsync(TodoTask task)
	{
		await gate.WaitAsync();
		try
		{
			Done.Remove(task);
			task.IsCompleted = false;
			task.CompletedDate = null;
			task.RawLine = null;
			Pending.Add(task);
			await WriteBothAsync();
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task DeleteAsync(TodoTask task)
	{
		await gate.WaitAsync();
		try
		{
			Pending.Remove(task);
			Done.Remove(task);
			await WriteBothAsync();

			// Delete attachment files that no surviving task references
			var referenced = Pending.Concat(Done)
				.SelectMany(t => t.Attachments)
				.Select(a => a.FileName)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var attachment in task.Attachments)
			{
				if (!referenced.Contains(attachment.FileName) && attachment.FullPath is { } path && File.Exists(path))
					File.Delete(path);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task UpdateBodyAsync(TodoTask task, string body)
	{
		await gate.WaitAsync();
		try
		{
			task.Body = body.Trim();
			task.RawLine = null;
			await WriteAsync(task.IsCompleted ? Done : Pending, task.IsCompleted ? DoneFilePath : TodoFilePath);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task SetPriorityAsync(TodoTask task, char? priority)
	{
		await gate.WaitAsync();
		try
		{
			task.Priority = priority;
			task.RawLine = null;
			await WriteAsync(task.IsCompleted ? Done : Pending, task.IsCompleted ? DoneFilePath : TodoFilePath);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Saves the clipboard image (if any) as a PNG and returns the attachment; null when the clipboard holds no image.</summary>
	public async Task<TodoAttachment?> AddAttachmentFromClipboardAsync()
	{
		var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
		if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
			return null;

		var reference = await content.GetBitmapAsync();
		return await AddAttachmentFromStreamAsync(reference);
	}

	/// <summary>Saves an image stream as a PNG attachment and returns it.</summary>
	public async Task<TodoAttachment?> AddAttachmentFromStreamAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
	{
		using var stream = await reference.OpenReadAsync();
		var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

		var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
		var path = Path.Combine(AttachmentsDirectory, fileName);
		using var output = File.Open(path, FileMode.CreateNew).AsRandomAccessStream();
		var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
		encoder.SetSoftwareBitmap(await decoder.GetSoftwareBitmapAsync());
		await encoder.FlushAsync();

		return new TodoAttachment { FileName = fileName, FullPath = path };
	}

	/// <summary>Registers an already-saved attachment on the task.</summary>
	public async Task RegisterAttachmentAsync(TodoTask task, TodoAttachment attachment)
	{
		await gate.WaitAsync();
		try
		{
			task.Attachments.Add(attachment);
			task.RawLine = null;
			await WriteAsync(task.IsCompleted ? Done : Pending, task.IsCompleted ? DoneFilePath : TodoFilePath);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Copies an image file into the attachments folder and registers it on the task.</summary>
	public async Task<TodoAttachment> AddAttachmentFromFileAsync(TodoTask task, string sourcePath)
	{
		var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}{Path.GetExtension(sourcePath)}";
		var destPath = Path.Combine(AttachmentsDirectory, fileName);
		File.Copy(sourcePath, destPath, overwrite: false);

		var attachment = new TodoAttachment { FileName = fileName, FullPath = destPath };
		await RegisterAttachmentAsync(task, attachment);
		return attachment;
	}

	/// <summary>Removes an attachment from the task and deletes its file if no other task references it.</summary>
	public async Task RemoveAttachmentAsync(TodoTask task, TodoAttachment attachment)
	{
		await gate.WaitAsync();
		try
		{
			task.Attachments.Remove(attachment);
			task.RawLine = null;
			await WriteAsync(task.IsCompleted ? Done : Pending, task.IsCompleted ? DoneFilePath : TodoFilePath);

			var referenced = Pending.Concat(Done)
				.SelectMany(t => t.Attachments)
				.Select(a => a.FileName)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			if (!referenced.Contains(attachment.FileName) && attachment.FullPath is { } path && File.Exists(path))
				File.Delete(path);
		}
		finally
		{
			gate.Release();
		}
	}

	private void FillAttachmentPaths(TodoTask task)
	{
		foreach (var attachment in task.Attachments)
			attachment.FullPath ??= Path.Combine(AttachmentsDirectory, attachment.FileName);
	}

	private async Task WriteBothAsync()
	{
		await WriteAsync(Pending, TodoFilePath);
		await WriteAsync(Done, DoneFilePath);
	}

	private async Task WriteAsync(List<TodoTask> tasks, string path)
	{
		var text = string.Join(Environment.NewLine, tasks.Select(t => t.Format())) + (tasks.Count > 0 ? Environment.NewLine : string.Empty);
		var tempPath = path + ".tmp";
		await File.WriteAllTextAsync(tempPath, text, new UTF8Encoding(false));
		File.Move(tempPath, path, overwrite: true);
	}

	private static async Task<List<string>> ReadLinesAsync(string path)
	{
		if (!File.Exists(path))
			return [];

		var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
		return [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
	}
}
