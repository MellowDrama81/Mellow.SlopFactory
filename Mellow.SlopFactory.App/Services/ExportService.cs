using CommunityToolkit.Maui.Storage;

namespace Mellow.SlopFactory.App.Services;

/// <summary>A file to export, at a path relative to the chosen folder (e.g. "Characters/hat.png").</summary>
public record ExportFile(string RelativePath, Func<Task<byte[]>> ReadAsync);

/// <summary>Saves images somewhere the user chooses, using the platform's save and folder dialogs.</summary>
public class ExportService
{
	private readonly IFileSaver fileSaver;
	private readonly IFolderPicker folderPicker;

	public ExportService(IFileSaver fileSaver, IFolderPicker folderPicker)
	{
		this.fileSaver = fileSaver;
		this.folderPicker = folderPicker;
	}

	/// <summary>Asks where to save one file and saves it. Returns the saved path, or null if the user cancelled.</summary>
	public async Task<string?> SaveAsync(string fileName, byte[] data, CancellationToken cancellationToken = default)
	{
		using var stream = new MemoryStream(data);
		var result = await fileSaver.SaveAsync(fileName, stream, cancellationToken);
		if (result.IsSuccessful)
		{
			return result.FilePath;
		}

		return IsCancellation(result.Exception) ? null : throw result.Exception ?? new IOException("The file couldn't be saved.");
	}

	/// <summary>
	/// Asks for a folder and writes the files into it, creating subfolders as needed. Existing files are never
	/// overwritten: a clashing name gets a number, like "hat (2).png". Returns the folder and how many files were
	/// written, or null if the user cancelled.
	/// </summary>
	public async Task<(string Folder, int Count)?> ExportAsync(IReadOnlyList<ExportFile> files, CancellationToken cancellationToken = default)
	{
		var result = await folderPicker.PickAsync(cancellationToken);
		if (!result.IsSuccessful)
		{
			return IsCancellation(result.Exception) ? null : throw result.Exception ?? new IOException("The folder couldn't be opened.");
		}

		var root = result.Folder.Path;
		var count = 0;
		foreach (var file in files)
		{
			var path = UniquePath(Path.Combine(root, file.RelativePath));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllBytesAsync(path, await file.ReadAsync(), cancellationToken);
			count++;
		}

		return (root, count);
	}

	/// <summary>Makes a name safe to use as a single file or folder name on any platform.</summary>
	public static string SafeName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
		var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
		return safe.Length == 0 ? "_" : safe;
	}

	/// <summary>The path itself if nothing is there yet, otherwise the first free "name (2).ext", "name (3).ext"...</summary>
	public static string UniquePath(string path)
	{
		if (!File.Exists(path))
		{
			return path;
		}

		var directory = Path.GetDirectoryName(path)!;
		var stem = Path.GetFileNameWithoutExtension(path);
		var extension = Path.GetExtension(path);
		for (var n = 2; ; n++)
		{
			var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
			if (!File.Exists(candidate))
			{
				return candidate;
			}
		}
	}

	// The toolkit reports a closed dialog as an unsuccessful result whose exception only has a message to go on:
	// "Operation cancelled." from the save dialog, and "Folder doesn't exist." from the folder dialog on Windows.
	private static bool IsCancellation(Exception? exception)
	{
		return exception is null or OperationCanceledException
			|| exception.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
			|| (exception is FolderPickerException && exception.Message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase));
	}
}
