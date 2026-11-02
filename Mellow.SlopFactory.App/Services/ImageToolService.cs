using Mellow.SlopFactory.Core;

namespace Mellow.SlopFactory.App.Services;

public enum ImageTool
{
	/// <summary>Cut the subject out with BiRefNet, leaving its pixels unchanged.</summary>
	RemoveBackground,

	/// <summary>Make the image larger with SeedVR2, restoring detail.</summary>
	Upscale,
}

public enum ImageToolJobState
{
	Queued,
	Running,
	Done,
	Failed,
}

/// <summary>One image going through a tool, from queued to its result in the library.</summary>
/// <param name="Factor">For upscaling, how many times larger each side gets.</param>
/// <param name="InputUrl">Thumbnail of the input, for the page.</param>
public sealed class ImageToolJob(ImageTool tool, LibraryImage source, int factor, string inputUrl)
{
	public string Id { get; } = Guid.NewGuid().ToString("N");
	public ImageTool Tool { get; } = tool;
	public LibraryImage Source { get; } = source;
	public int Factor { get; } = factor;
	public string InputUrl { get; } = inputUrl;

	public ImageToolJobState State { get; internal set; } = ImageToolJobState.Queued;

	/// <summary>What's happening now, or why it failed.</summary>
	public string Status { get; internal set; } = "Waiting...";

	/// <summary>The result, once it's in the library.</summary>
	public LibraryImage? Result { get; internal set; }

	/// <summary>Thumbnail of the result, for the page.</summary>
	public string? ResultUrl { get; internal set; }

	public bool IsFinished => State is ImageToolJobState.Done or ImageToolJobState.Failed;
}

/// <summary>
/// Queues and runs the one-image tools (background removal and upscaling) on library images, one image at a time per
/// tool, and adds each result to the library next to its original. Jobs keep running while the user is on other
/// pages; the tool pages show them from here.
/// </summary>
public class ImageToolService
{
	private readonly SettingsService settingsService;
	private readonly LibraryService libraryService;
	private readonly ThumbnailService thumbnailService;
	private readonly ImageGenerationService imageGenerationService;
	private readonly Dictionary<ImageTool, List<ImageToolJob>> jobs = new()
	{
		[ImageTool.RemoveBackground] = [],
		[ImageTool.Upscale] = [],
	};
	private readonly HashSet<ImageTool> working = [];

	public ImageToolService(SettingsService settingsService, LibraryService libraryService, ThumbnailService thumbnailService, ImageGenerationService imageGenerationService)
	{
		this.settingsService = settingsService;
		this.libraryService = libraryService;
		this.thumbnailService = thumbnailService;
		this.imageGenerationService = imageGenerationService;
	}

	/// <summary>Raised when a job is added, reports progress, finishes or is cleared.</summary>
	public event Action? Changed;

	/// <summary>How many times larger each side gets when upscaling (one of <see cref="ImageToolWorkflows.UpscaleFactors"/>).</summary>
	public int UpscaleFactor { get; set; } = 2;

	/// <summary>A tool's jobs, oldest first.</summary>
	public IReadOnlyList<ImageToolJob> Jobs(ImageTool tool) => jobs[tool];

	/// <summary>Whether a tool has jobs waiting or running.</summary>
	public bool IsBusy(ImageTool tool) => jobs[tool].Any(j => !j.IsFinished);

	public static string Describe(ImageTool tool, int factor)
	{
		return tool == ImageTool.RemoveBackground ? "Remove background" : $"Upscale {factor}×";
	}

	/// <summary>Queues library images for a tool and starts working through them if it isn't already.</summary>
	/// <param name="images">Each image with its thumbnail data URL.</param>
	public void Enqueue(ImageTool tool, IEnumerable<(LibraryImage Image, string ThumbnailUrl)> images)
	{
		foreach (var (image, thumbnailUrl) in images)
		{
			jobs[tool].Add(new ImageToolJob(tool, image, UpscaleFactor, thumbnailUrl));
		}

		Changed?.Invoke();
		if (working.Add(tool))
		{
			_ = WorkAsync(tool);
		}
	}

	/// <summary>Removes a tool's finished jobs from the list. Their results stay in the library.</summary>
	public void ClearFinished(ImageTool tool)
	{
		jobs[tool].RemoveAll(j => j.IsFinished);
		Changed?.Invoke();
	}

	private async Task WorkAsync(ImageTool tool)
	{
		try
		{
			while (jobs[tool].FirstOrDefault(j => j.State == ImageToolJobState.Queued) is { } job)
			{
				job.State = ImageToolJobState.Running;
				try
				{
					await RunAsync(job);
					job.State = ImageToolJobState.Done;
					job.Status = "Added to the library.";
				}
				catch (Exception ex)
				{
					job.State = ImageToolJobState.Failed;
					job.Status = ex.Message;
				}

				Changed?.Invoke();
			}
		}
		finally
		{
			working.Remove(tool);
		}
	}

	private async Task RunAsync(ImageToolJob job)
	{
		var settings = await settingsService.LoadComfyAsync();
		using var comfy = new ComfyApi(settings);
		var input = new InputImage(job.Source.FileName, await libraryService.ReadAsync(job.Source));
		var progress = new Progress<string>(message => SetStatus(job, message));
		var file = await imageGenerationService.PutOnServerAsync(comfy, input, job.Source.FileName, progress, CancellationToken.None);
		var name = string.IsNullOrEmpty(file.Subfolder) ? file.Filename : $"{file.Subfolder}/{file.Filename}";

		var workflow = job.Tool == ImageTool.RemoveBackground ? ImageToolWorkflows.RemoveBackground(name) : ImageToolWorkflows.Upscale(name, job.Factor);
		SetStatus(job, job.Tool == ImageTool.RemoveBackground ? "Removing the background..." : $"Upscaling {job.Factor}×...");
		var outputs = await comfy.RunWorkflowAsync(workflow);
		var output = outputs.FirstOrDefault() ?? throw new ComfyApiException("The workflow returned no image.");

		var suffix = job.Tool == ImageTool.RemoveBackground ? "no background" : $"upscaled {job.Factor}x";
		var fileName = $"{Path.GetFileNameWithoutExtension(job.Source.FileName)} ({suffix}){Path.GetExtension(output.File.Filename)}";
		var thumbnail = await thumbnailService.CreateAsync(output.Data, LibraryService.ContentTypeFor(fileName));
		var (result, _) = await libraryService.AddAsync(fileName, output.Data, thumbnail, job.Source.FolderId,
			new LibraryImageMetadata(LibraryImageSource.Generated, job.Source.Metadata?.Prompt, Workflow: Describe(job.Tool, job.Factor)));
		job.Result = result;

		// Thumbnails are JPEGs on white, which would hide the transparency, so a cutout is shown as itself.
		job.ResultUrl = job.Tool == ImageTool.RemoveBackground
			? $"data:{LibraryService.ContentTypeFor(fileName)};base64,{Convert.ToBase64String(output.Data)}"
			: await libraryService.GetThumbnailDataUrlAsync(result);
	}

	private void SetStatus(ImageToolJob job, string message)
	{
		job.Status = message;
		Changed?.Invoke();
	}
}
