using Mellow.SlopFactory.Core;

namespace Mellow.SlopFactory.App.Services;

public record InputImage(string FileName, byte[] Data);

/// <param name="Workflow">The name of the workflow that made it.</param>
public record GeneratedImage(string FilePath, byte[] Data, string Prompt, string Workflow);

public class ImageGenerationRequest
{
	public string Prompt { get; set; } = string.Empty;

	/// <summary>0 images generates from text; 1 to 10 edits image 1 using the rest as references.</summary>
	public IReadOnlyList<InputImage> Images { get; set; } = [];

	/// <summary>
	/// A PNG mask for image 1, white where it should change. With a mask, only that area is redrawn (inpainting)
	/// and the rest of image 1 is kept exactly. Ignored with no images.
	/// </summary>
	public byte[]? Mask { get; set; }

	/// <summary>
	/// Which workflow to run (an <see cref="ImageWorkflows"/> id). One that doesn't suit the inputs, or null, uses the
	/// default for them.
	/// </summary>
	public string? WorkflowId { get; set; }

	public bool RefinePrompt { get; set; }
	public bool Thinking { get; set; }

	/// <summary>
	/// What to call the saved image, without an extension: the extension ComfyUI returns is kept. Null or blank
	/// names it by date and time. Several images from one run get "-2", "-3"... after the first.
	/// </summary>
	public string? FileName { get; set; }

	/// <summary>Output size for text to image. Edits follow image 1.</summary>
	public int Width { get; set; } = 1024;
	public int Height { get; set; } = 1024;
}

/// <summary>
/// Runs the built-in workflows (see <see cref="ImageWorkflows"/>): text to image with no input images, edit with 1 to
/// 10, and inpainting when image 1 has a mask.
/// </summary>
public class ImageGenerationService
{
	public const int MaxImages = QwenImageEditOptions.MaxImages;

	private readonly SettingsService settingsService;
	private readonly LibraryService libraryService;

	public ImageGenerationService(SettingsService settingsService, LibraryService libraryService)
	{
		this.settingsService = settingsService;
		this.libraryService = libraryService;
	}

	/// <summary>Where generated images are saved.</summary>
	public static string OutputDirectory => Path.Combine(FileSystem.AppDataDirectory, "Outputs");

	public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(request.Prompt))
		{
			throw new ArgumentException("Enter a prompt.", nameof(request));
		}

		if (request.Images.Count > MaxImages)
		{
			throw new ArgumentException($"At most {MaxImages} images are supported.", nameof(request));
		}

		var settings = await settingsService.LoadComfyAsync();
		using var comfy = new ComfyApi(settings);

		var chosen = ImageWorkflows.Resolve(ImageWorkflows.KindFor(request.Images.Count, request.Mask != null), request.WorkflowId);
		System.Text.Json.Nodes.JsonObject workflow;
		if (chosen.Id == ImageWorkflows.Flux3KleinGenerate)
		{
			workflow = Flux3KleinGenerateWorkflow.Build(new Flux3KleinGenerateOptions
			{
				Prompt = request.Prompt,
				UpsamplePrompt = request.RefinePrompt,
				Thinking = request.Thinking,
				Width = request.Width,
				Height = request.Height,
			});
		}
		else if (chosen.Id == ImageWorkflows.QwenGenerate)
		{
			workflow = QwenImageGenerateWorkflow.Build(new QwenImageGenerateOptions
			{
				Prompt = request.Prompt,
				RefinePrompt = request.RefinePrompt,
				Thinking = request.Thinking,
				Width = request.Width,
				Height = request.Height,
			});
		}
		else
		{
			var uploaded = new List<string>();
			for (var i = 0; i < request.Images.Count; i++)
			{
				var file = await PutOnServerAsync(comfy, request.Images[i], $"image {i + 1} of {request.Images.Count}", progress, cancellationToken);
				uploaded.Add(string.IsNullOrEmpty(file.Subfolder) ? file.Filename : $"{file.Subfolder}/{file.Filename}");
			}

			if (chosen.Id == ImageWorkflows.QwenInpaint && request.Mask is { } maskData)
			{
				progress?.Report("Uploading the mask...");
				using var maskStream = new MemoryStream(maskData);
				var mask = await comfy.UploadImageAsync(maskStream, $"mask-{LibraryService.IdFor(maskData)}.png", cancellationToken: cancellationToken);
				workflow = QwenImageInpaintWorkflow.Build(new QwenImageInpaintOptions
				{
					Prompt = request.Prompt,
					Images = uploaded,
					Mask = string.IsNullOrEmpty(mask.Subfolder) ? mask.Filename : $"{mask.Subfolder}/{mask.Filename}",
					RefinePrompt = request.RefinePrompt,
					Thinking = request.Thinking,
				});
			}
			else
			{
				workflow = QwenImageEditWorkflow.Build(new QwenImageEditOptions
				{
					Prompt = request.Prompt,
					Images = uploaded,
					RefinePrompt = request.RefinePrompt,
					Thinking = request.Thinking,
				});
			}
		}

		progress?.Report(request.RefinePrompt ? $"Improving the prompt and rendering with {chosen.Name}..." : $"Rendering with {chosen.Name}...");
		var outputs = await comfy.RunWorkflowAsync(workflow, cancellationToken);

		Directory.CreateDirectory(OutputDirectory);
		var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
		var name = CleanFileName(request.FileName);
		var results = new List<GeneratedImage>();
		for (var i = 0; i < outputs.Count; i++)
		{
			var extension = Path.GetExtension(outputs[i].File.Filename);
			var stem = name == null ? $"{stamp}-{i + 1}" : i == 0 ? name : $"{name}-{i + 1}";
			var path = ExportService.UniquePath(Path.Combine(OutputDirectory, stem + extension));
			await File.WriteAllBytesAsync(path, outputs[i].Data, cancellationToken);
			results.Add(new GeneratedImage(path, outputs[i].Data, request.Prompt, chosen.Name));
		}

		// The prompt goes next to the first image, under the same name.
		var promptFile = results.Count > 0 ? Path.ChangeExtension(results[0].FilePath, ".txt") : Path.Combine(OutputDirectory, $"{stamp}.txt");
		await File.WriteAllTextAsync(promptFile, request.Prompt, cancellationToken);
		return results;
	}

	/// <summary>
	/// A requested file name made safe to save, with any image extension the user typed removed (the returned image's
	/// own extension is used instead). Null if nothing usable was given.
	/// </summary>
	public static string? CleanFileName(string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		var name = fileName.Trim();
		if (Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
		{
			name = Path.GetFileNameWithoutExtension(name);
		}

		return string.IsNullOrWhiteSpace(name) ? null : ExportService.SafeName(name);
	}

	/// <summary>
	/// Gets an input image into the server's input folder: reuses the copy the library recorded for this server if
	/// the server still has it, otherwise uploads it and records the new copy.
	/// </summary>
	private async Task<ComfyFileRef> PutOnServerAsync(ComfyApi comfy, InputImage input, string label, IProgress<string>? progress, CancellationToken cancellationToken)
	{
		var server = ComfyServer.Key(comfy.BaseAddress);
		var image = await libraryService.FindAsync(input.Data);
		if (image?.Metadata?.ComfyCopies?.FirstOrDefault(c => c.Server == server) is { } copy)
		{
			progress?.Report($"Checking {label} is still on the server...");
			var file = new ComfyFileRef(copy.Filename, copy.Subfolder, copy.Type);
			try
			{
				if (await comfy.FileExistsAsync(file, cancellationToken))
				{
					return file;
				}
			}
			catch (ComfyApiException)
			{
				// Can't tell, so upload it again to be safe.
			}

			await libraryService.RemoveComfyCopyAsync(image.Id, server);
		}

		progress?.Report($"Uploading {label}...");

		// Name the upload after its contents so two different images with the same name can't overwrite each other.
		var extension = Path.GetExtension(input.FileName) is { Length: > 0 } ext ? ext : ".png";
		using var stream = new MemoryStream(input.Data);
		var uploaded = await comfy.UploadImageAsync(stream, LibraryService.IdFor(input.Data) + extension, cancellationToken: cancellationToken);
		if (image != null)
		{
			await libraryService.SetComfyCopyAsync(image.Id, new ComfyCopy(server, uploaded.Filename, uploaded.Subfolder, uploaded.Type ?? "input", DateTimeOffset.Now));
		}

		return uploaded;
	}
}
