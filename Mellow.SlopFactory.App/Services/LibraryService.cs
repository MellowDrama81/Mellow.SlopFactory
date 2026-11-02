using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.App.Services;

/// <param name="FolderId">The folder the image is in, or null for the top of the library.</param>
/// <param name="Metadata">Null for images added before metadata was recorded.</param>
public record LibraryImage(string Id, string FileName, DateTimeOffset AddedAt, string ContentType, string? FolderId = null, LibraryImageMetadata? Metadata = null);

public static class LibraryImageSource
{
	public const string Uploaded = "uploaded";
	public const string Generated = "generated";
}

/// <param name="Source">How the image got into the library: <see cref="LibraryImageSource"/>.</param>
/// <param name="Prompt">For a generated image, the prompt it was generated from.</param>
/// <param name="ComfyCopies">Where ComfyUI servers already have this image, so it needn't be uploaded again.</param>
/// <param name="Workflow">For a generated image, the name of the workflow that made it.</param>
public record LibraryImageMetadata(string? Source = null, string? Prompt = null, IReadOnlyList<ComfyCopy>? ComfyCopies = null, string? Workflow = null);

/// <summary>A copy of a library image in a ComfyUI server's input folder.</summary>
/// <param name="Server">The server's base address, as from <see cref="ComfyServer.Key"/>.</param>
public record ComfyCopy(string Server, string Filename, string? Subfolder, string Type, DateTimeOffset UploadedAt);

/// <summary>A library change that can't be made, with a message to show the user.</summary>
public class LibraryException(string message) : Exception(message);

/// <param name="ParentId">The folder this one is in, or null for the top of the library.</param>
public record LibraryFolder(string Id, string Name, string? ParentId, DateTimeOffset CreatedAt);

/// <summary>
/// Images the user keeps for reuse as inputs, optionally organized into nested folders. Stored in the app data
/// folder as the original file plus a small JPEG thumbnail, with an index of names, dates and folders.
/// Identical images are stored once. Folders exist only in the index; the files stay in one directory.
/// </summary>
public class LibraryService
{
	private const string IndexFileName = "library.json";
	private const string ThumbnailSuffix = ".thumb.jpg";

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly SemaphoreSlim gate = new(1, 1);
	private LibraryIndex? index;

	public static string LibraryDirectory => Path.Combine(FileSystem.AppDataDirectory, "Library");

	/// <summary>Raised after images or folders are added, changed or removed.</summary>
	public event Action? Changed;

	/// <summary>All library images, newest first.</summary>
	public async Task<IReadOnlyList<LibraryImage>> GetAllAsync()
	{
		await gate.WaitAsync();
		try
		{
			return (await LoadIndexAsync()).Images.OrderByDescending(i => i.AddedAt).ToList();
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>All folders, sorted by name.</summary>
	public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync()
	{
		await gate.WaitAsync();
		try
		{
			return (await LoadIndexAsync()).Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Adds an image to a folder (null for the top of the library), or returns the existing entry, left where
	/// it is, if the same image is already in the library.
	/// </summary>
	public async Task<(LibraryImage Image, bool Added)> AddAsync(string fileName, byte[] data, byte[]? thumbnail, string? folderId = null, LibraryImageMetadata? metadata = null)
	{
		var id = IdFor(data);

		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			var existing = library.Images.FirstOrDefault(i => i.Id == id);
			if (existing != null)
			{
				return (existing, false);
			}

			if (folderId != null && library.Folders.All(f => f.Id != folderId))
			{
				folderId = null;
			}

			Directory.CreateDirectory(LibraryDirectory);
			var image = new LibraryImage(id, Path.GetFileName(fileName), DateTimeOffset.Now, ContentTypeFor(fileName), folderId, metadata);
			await File.WriteAllBytesAsync(ImagePath(image), data);
			if (thumbnail != null)
			{
				await File.WriteAllBytesAsync(ThumbnailPath(image), thumbnail);
			}

			library.Images.Add(image);
			await SaveIndexAsync(library);
			Changed?.Invoke();
			return (image, true);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>The library's id for an image: a hash of its contents, so the same image always gets the same id.</summary>
	public static string IdFor(byte[] data)
	{
		return Convert.ToHexString(SHA256.HashData(data))[..32].ToLowerInvariant();
	}

	/// <summary>The library entry with these exact contents, if there is one.</summary>
	public async Task<LibraryImage?> FindAsync(byte[] data)
	{
		var id = IdFor(data);
		await gate.WaitAsync();
		try
		{
			return (await LoadIndexAsync()).Images.FirstOrDefault(i => i.Id == id);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Records where a server has a copy of an image, replacing any copy recorded earlier for that server.</summary>
	public Task SetComfyCopyAsync(string id, ComfyCopy copy)
	{
		return UpdateComfyCopiesAsync(id, copy.Server, copy);
	}

	/// <summary>Forgets a server's copy of an image, e.g. because the server no longer has it.</summary>
	public Task RemoveComfyCopyAsync(string id, string server)
	{
		return UpdateComfyCopiesAsync(id, server, null);
	}

	private async Task UpdateComfyCopiesAsync(string id, string server, ComfyCopy? copy)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			var position = library.Images.FindIndex(i => i.Id == id);
			if (position < 0)
			{
				return;
			}

			var image = library.Images[position];
			var metadata = image.Metadata ?? new LibraryImageMetadata();
			var copies = (metadata.ComfyCopies ?? []).Where(c => c.Server != server).ToList();
			if (copy != null)
			{
				copies.Add(copy);
			}

			library.Images[position] = image with { Metadata = metadata with { ComfyCopies = copies } };
			await SaveIndexAsync(library);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task DeleteAsync(string id)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			var image = library.Images.FirstOrDefault(i => i.Id == id);
			if (image == null)
			{
				return;
			}

			library.Images.Remove(image);
			await SaveIndexAsync(library);
			File.Delete(ImagePath(image));
			File.Delete(ThumbnailPath(image));
			Changed?.Invoke();
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Creates a folder inside <paramref name="parentId"/> (null for the top of the library).</summary>
	/// <exception cref="LibraryException">The name is empty or already used in that folder.</exception>
	public async Task<LibraryFolder> CreateFolderAsync(string name, string? parentId)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			if (parentId != null && library.Folders.All(f => f.Id != parentId))
			{
				throw new LibraryException("The parent folder no longer exists.");
			}

			name = ValidateFolderName(library, name, parentId, exceptId: null);
			var folder = new LibraryFolder(Guid.NewGuid().ToString("N"), name, parentId, DateTimeOffset.Now);
			library.Folders.Add(folder);
			await SaveIndexAsync(library);
			Changed?.Invoke();
			return folder;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <exception cref="LibraryException">The name is empty or already used next to this folder.</exception>
	public async Task RenameFolderAsync(string id, string name)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			var position = library.Folders.FindIndex(f => f.Id == id);
			if (position < 0)
			{
				return;
			}

			var folder = library.Folders[position];
			library.Folders[position] = folder with { Name = ValidateFolderName(library, name, folder.ParentId, exceptId: id) };
			await SaveIndexAsync(library);
			Changed?.Invoke();
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Deletes a folder without deleting anything in it: its images and subfolders move up into its parent.
	/// </summary>
	public async Task DeleteFolderAsync(string id)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			var folder = library.Folders.FirstOrDefault(f => f.Id == id);
			if (folder == null)
			{
				return;
			}

			library.Folders.Remove(folder);
			for (var i = 0; i < library.Folders.Count; i++)
			{
				if (library.Folders[i].ParentId == id)
				{
					library.Folders[i] = library.Folders[i] with { ParentId = folder.ParentId };
				}
			}

			for (var i = 0; i < library.Images.Count; i++)
			{
				if (library.Images[i].FolderId == id)
				{
					library.Images[i] = library.Images[i] with { FolderId = folder.ParentId };
				}
			}

			await SaveIndexAsync(library);
			Changed?.Invoke();
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Moves images and folders into <paramref name="targetFolderId"/> (null for the top of the library).</summary>
	/// <exception cref="LibraryException">
	/// A folder would end up inside itself, or next to a folder with the same name.
	/// </exception>
	public async Task MoveAsync(IEnumerable<string> imageIds, IEnumerable<string> folderIds, string? targetFolderId)
	{
		await gate.WaitAsync();
		try
		{
			var library = await LoadIndexAsync();
			if (targetFolderId != null && library.Folders.All(f => f.Id != targetFolderId))
			{
				throw new LibraryException("The destination folder no longer exists.");
			}

			var movingFolders = folderIds.ToHashSet();
			foreach (var folder in library.Folders.Where(f => movingFolders.Contains(f.Id)))
			{
				if (folder.Id == targetFolderId || IsInside(library, targetFolderId, folder.Id))
				{
					throw new LibraryException($"Can't move \"{folder.Name}\" into itself.");
				}
			}

			// Check names before changing anything so a clash doesn't leave a half-finished move.
			var names = library.Folders
				.Where(f => f.ParentId == targetFolderId && !movingFolders.Contains(f.Id))
				.Select(f => f.Name)
				.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
			foreach (var folder in library.Folders.Where(f => movingFolders.Contains(f.Id) && f.ParentId != targetFolderId))
			{
				if (!names.Add(folder.Name))
				{
					throw new LibraryException($"There's already a folder called \"{folder.Name}\" there.");
				}
			}

			for (var i = 0; i < library.Folders.Count; i++)
			{
				if (movingFolders.Contains(library.Folders[i].Id))
				{
					library.Folders[i] = library.Folders[i] with { ParentId = targetFolderId };
				}
			}

			var movingImages = imageIds.ToHashSet();
			for (var i = 0; i < library.Images.Count; i++)
			{
				if (movingImages.Contains(library.Images[i].Id))
				{
					library.Images[i] = library.Images[i] with { FolderId = targetFolderId };
				}
			}

			await SaveIndexAsync(library);
			Changed?.Invoke();
		}
		finally
		{
			gate.Release();
		}
	}

	public Task<byte[]> ReadAsync(LibraryImage image)
	{
		return File.ReadAllBytesAsync(ImagePath(image));
	}

	/// <summary>A data URL for showing the image small: the thumbnail if there is one, otherwise the image itself.</summary>
	public async Task<string> GetThumbnailDataUrlAsync(LibraryImage image)
	{
		var thumbnailPath = ThumbnailPath(image);
		return File.Exists(thumbnailPath)
			? $"data:image/jpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(thumbnailPath))}"
			: $"data:{image.ContentType};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(ImagePath(image)))}";
	}

	/// <summary>The folders from the top of the library down to <paramref name="folderId"/>, inclusive.</summary>
	public static IReadOnlyList<LibraryFolder> PathTo(IReadOnlyList<LibraryFolder> folders, string? folderId)
	{
		var path = new List<LibraryFolder>();
		var byId = folders.ToDictionary(f => f.Id);
		while (folderId != null && byId.TryGetValue(folderId, out var folder) && path.Count <= folders.Count)
		{
			path.Insert(0, folder);
			folderId = folder.ParentId;
		}

		return path;
	}

	public static string ImagePath(LibraryImage image)
	{
		return Path.Combine(LibraryDirectory, image.Id + ExtensionFor(image.ContentType));
	}

	private static string ThumbnailPath(LibraryImage image)
	{
		return Path.Combine(LibraryDirectory, image.Id + ThumbnailSuffix);
	}

	public static string ContentTypeFor(string fileName)
	{
		return Path.GetExtension(fileName).ToLowerInvariant() switch
		{
			".jpg" or ".jpeg" => "image/jpeg",
			".webp" => "image/webp",
			_ => "image/png",
		};
	}

	private static string ExtensionFor(string contentType)
	{
		return contentType switch
		{
			"image/jpeg" => ".jpg",
			"image/webp" => ".webp",
			_ => ".png",
		};
	}

	private static string ValidateFolderName(LibraryIndex library, string name, string? parentId, string? exceptId)
	{
		name = name.Trim();
		if (name.Length == 0)
		{
			throw new LibraryException("Enter a folder name.");
		}

		if (library.Folders.Any(f => f.ParentId == parentId && f.Id != exceptId && string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase)))
		{
			throw new LibraryException($"There's already a folder called \"{name}\" here.");
		}

		return name;
	}

	/// <summary>Whether <paramref name="folderId"/> is somewhere inside <paramref name="ancestorId"/>.</summary>
	private static bool IsInside(LibraryIndex library, string? folderId, string ancestorId)
	{
		var seen = new HashSet<string>();
		while (folderId != null && seen.Add(folderId))
		{
			var folder = library.Folders.FirstOrDefault(f => f.Id == folderId);
			if (folder == null)
			{
				return false;
			}

			if (folder.ParentId == ancestorId)
			{
				return true;
			}

			folderId = folder.ParentId;
		}

		return false;
	}

	private async Task<LibraryIndex> LoadIndexAsync()
	{
		if (index != null)
		{
			return index;
		}

		var path = Path.Combine(LibraryDirectory, IndexFileName);
		if (File.Exists(path))
		{
			await using var stream = File.OpenRead(path);
			var node = await JsonNode.ParseAsync(stream);

			// Libraries from before folders were added are a plain list of images.
			index = node is JsonArray
				? new LibraryIndex { Images = node.Deserialize<List<LibraryImage>>(JsonOptions) ?? [] }
				: node.Deserialize<LibraryIndex>(JsonOptions) ?? new LibraryIndex();

			// Drop entries whose files were removed outside the app, and put images whose folder is gone back at the top.
			index.Images.RemoveAll(i => !File.Exists(ImagePath(i)));
			var folderIds = index.Folders.Select(f => f.Id).ToHashSet();
			for (var i = 0; i < index.Folders.Count; i++)
			{
				if (index.Folders[i].ParentId is { } parentId && !folderIds.Contains(parentId))
				{
					index.Folders[i] = index.Folders[i] with { ParentId = null };
				}
			}

			for (var i = 0; i < index.Images.Count; i++)
			{
				if (index.Images[i].FolderId is { } folderId && !folderIds.Contains(folderId))
				{
					index.Images[i] = index.Images[i] with { FolderId = null };
				}
			}
		}
		else
		{
			index = new LibraryIndex();
		}

		return index;
	}

	private async Task SaveIndexAsync(LibraryIndex library)
	{
		Directory.CreateDirectory(LibraryDirectory);
		var path = Path.Combine(LibraryDirectory, IndexFileName);
		var temp = path + ".tmp";
		await using (var stream = File.Create(temp))
		{
			await JsonSerializer.SerializeAsync(stream, library, JsonOptions);
		}

		File.Move(temp, path, overwrite: true);
	}

	private class LibraryIndex
	{
		public List<LibraryFolder> Folders { get; set; } = [];

		public List<LibraryImage> Images { get; set; } = [];
	}
}
