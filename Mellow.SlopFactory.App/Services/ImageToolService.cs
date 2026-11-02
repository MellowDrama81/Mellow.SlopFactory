using Mellow.SlopFactory.Core;

namespace Mellow.SlopFactory.App.Services;

public enum ImageTool
{
	/// <summary>Cut the subject out with BiRefNet, leaving its pixels unchanged.</summary>
	RemoveBackground,

	/// <summary>Make the image larger with SeedVR2, restoring detail.</summary>
	Upscale,
}

/// <summary>
/// Runs the one-image tools (background removal and upscaling) on library images and adds each result to the
/// library next to its original. Jobs keep running if the page that started them is left.
/// </summary>
public class ImageToolService
{
	private readonly SettingsService settingsService;
	private readonly LibraryService libraryService;
	private readonly ThumbnailService thumbnailService;
	private readonly ImageGenerationService imageGenerationService;
	private readonly Dictionary<string, string> running = [];

	public ImageToolService(SettingsService settingsService, LibraryService libraryService, ThumbnailService thumbnailService, ImageGenerationService imageGenerationService)
	{
		this.settingsService = settingsService;
		this.libraryService = libraryService;
		this.thumbnailService = thumbnailService;
		this.imageGenerationService = imageGenerationService;
	}

	/// <summary>Raised when a job starts, reports progress or finishes.</summary>
	public event Action? Changed;

	/// <summary>How many times larger each side gets when upscaling (one of <see cref="ImageToolWorkflows.UpscaleFactors"/>).</summary>
	public int UpscaleFactor { get; set; } = 2;

	/// <summary>What's happening to each library image a tool is running on, by image id.</summary>
	public IReadOnlyDictionary<string, string> Running => running;

	public static string Describe(ImageTool tool, int factor)
	{
		return tool == ImageTool.RemoveBackground ? "Remove background" : $"Upscale {factor}×";
	}

	/// <summary>
	/// Runs a tool on a library image and adds the result to the same folder. Returns the new library image, or the
	/// existing one if the result was already in the library.
	/// </summary>
	public async Task<LibraryImage> RunAsync(LibraryImage image, ImageTool tool, CancellationToken cancellationToken = default)
	{
		if (running.ContainsKey(image.Id))
		{
			throw new InvalidOperationException($"{image.FileName} is already being worked on.");
		}

		var factor = UpscaleFactor;
		SetStatus(image.Id, "Starting...");
		try
		{
			var settings = await settingsService.LoadComfyAsync();
			using var comfy = new ComfyApi(settings);
			var input = new InputImage(image.FileName, await libraryService.ReadAsync(image));
			var progress = new Progress<string>(message => SetStatus(image.Id, message));
			var file = await imageGenerationService.PutOnServerAsync(comfy, input, image.FileName, progress, cancellationToken);
			var name = string.IsNullOrEmpty(file.Subfolder) ? file.Filename : $"{file.Subfolder}/{file.Filename}";

			var workflow = tool == ImageTool.RemoveBackground ? ImageToolWorkflows.RemoveBackground(name) : ImageToolWorkflows.Upscale(name, factor);
			SetStatus(image.Id, tool == ImageTool.RemoveBackground ? "Removing the background..." : $"Upscaling {factor}×...");
			var outputs = await comfy.RunWorkflowAsync(workflow, cancellationToken);
			var output = outputs.FirstOrDefault() ?? throw new ComfyApiException("The workflow returned no image.");

			var suffix = tool == ImageTool.RemoveBackground ? "no background" : $"upscaled {factor}x";
			var fileName = $"{Path.GetFileNameWithoutExtension(image.FileName)} ({suffix}){Path.GetExtension(output.File.Filename)}";
			var thumbnail = await thumbnailService.CreateAsync(output.Data, LibraryService.ContentTypeFor(fileName));
			var (result, _) = await libraryService.AddAsync(fileName, output.Data, thumbnail, image.FolderId,
				new LibraryImageMetadata(LibraryImageSource.Generated, image.Metadata?.Prompt, Workflow: Describe(tool, factor)));
			return result;
		}
		finally
		{
			running.Remove(image.Id);
			Changed?.Invoke();
		}
	}

	private void SetStatus(string imageId, string message)
	{
		running[imageId] = message;
		Changed?.Invoke();
	}
}
