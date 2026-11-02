namespace Mellow.SlopFactory.App.Services;

/// <summary>
/// The Create page's form, results and running generation. It lives outside the page so a generation keeps
/// running, and its results still reach the library, while the user is on another page. Registered as scoped,
/// which in a Blazor Hybrid app means one per WebView, so it lasts as long as the app's UI.
/// </summary>
public class CreateSession
{
	public static readonly (string Key, string Label, int Width, int Height)[] Sizes =
	[
		("square", "Square (1024 × 1024)", 1024, 1024),
		("landscape", "Landscape (1536 × 1024)", 1536, 1024),
		("portrait", "Portrait (1024 × 1536)", 1024, 1536),
		("wide", "Wide (1792 × 1024)", 1792, 1024),
		("square-2k", "Square 2K (2048 × 2048)", 2048, 2048),
	];

	private readonly ImageGenerationService imageGenerationService;
	private readonly LibraryService libraryService;
	private readonly ThumbnailService thumbnailService;
	private CancellationTokenSource? cts;

	public CreateSession(ImageGenerationService imageGenerationService, LibraryService libraryService, ThumbnailService thumbnailService)
	{
		this.imageGenerationService = imageGenerationService;
		this.libraryService = libraryService;
		this.thumbnailService = thumbnailService;
	}

	public List<InputItem> Images { get; } = [];

	public List<ResultItem> Results { get; } = [];

	public string? Prompt { get; set; }

	public string SizeKey { get; set; } = "square";

	public bool RefinePrompt { get; set; } = true;

	public bool Thinking { get; set; } = true;

	/// <summary>The library folder this generation's images go into: "" for the top of the library.</summary>
	public string OutputFolderId { get; set; } = "";

	/// <summary>
	/// Whether the user picked <see cref="OutputFolderId"/> themselves. Until they do, the Create page keeps it in
	/// step with the folder configured in Settings.
	/// </summary>
	public bool OutputFolderChosen { get; set; }

	/// <summary>The workflow the inputs call for now: generate, edit or inpaint.</summary>
	public ImageWorkflowKind WorkflowKind => ImageWorkflows.KindFor(Images.Count, Images.Count > 0 && Images[0].Mask != null);

	/// <summary>The chosen workflow for each kind, so switching between them keeps each choice.</summary>
	private readonly Dictionary<ImageWorkflowKind, string> workflowChoices = [];

	/// <summary>The workflow that will run for the current inputs; null if none of this kind takes this many images.</summary>
	public ImageWorkflow? Workflow => ImageWorkflows.Resolve(WorkflowKind, Images.Count, workflowChoices.GetValueOrDefault(WorkflowKind));

	public void ChooseWorkflow(string? id)
	{
		if (id != null)
		{
			workflowChoices[WorkflowKind] = id;
		}
	}

	/// <summary>The name to save the generated image as, without an extension. Blank names it by date and time.</summary>
	public string? FileName { get; set; }

	public bool Busy { get; private set; }

	public string? Status { get; set; }

	public bool StatusIsError { get; set; }

	/// <summary>Set when a generation finishes with images, until the page has scrolled them into view.</summary>
	public bool HasNewResults { get; set; }

	/// <summary>Raised whenever progress, status or results change, possibly while no page is showing them.</summary>
	public event Action? Changed;

	/// <summary>Starts a generation from the current form. It runs until it finishes or <see cref="Cancel"/> is called.</summary>
	public async Task GenerateAsync()
	{
		if (Busy || string.IsNullOrWhiteSpace(Prompt) || Workflow == null)
		{
			return;
		}

		var size = Sizes.First(s => s.Key == SizeKey);
		var request = new ImageGenerationRequest
		{
			Prompt = Prompt.Trim(),
			Images = Images.Select(i => i.Input).ToList(),
			Mask = Images.Count > 0 ? Images[0].Mask : null,
			RefinePrompt = RefinePrompt,
			Thinking = RefinePrompt && Thinking,
			Width = size.Width,
			Height = size.Height,
			FileName = FileName,
			WorkflowId = Workflow?.Id,
		};
		var folderId = OutputFolderId.Length == 0 ? null : OutputFolderId;

		Busy = true;
		StatusIsError = false;
		Status = "Starting...";
		cts = new CancellationTokenSource();
		Changed?.Invoke();
		var progress = new Progress<string>(message =>
		{
			Status = message;
			Changed?.Invoke();
		});

		var started = DateTime.Now;
		try
		{
			var generated = await imageGenerationService.GenerateAsync(request, progress, cts.Token);
			Results.Clear();
			Results.AddRange(generated.Select(g => new ResultItem(g, $"data:image/png;base64,{Convert.ToBase64String(g.Data)}") { LibraryFolderId = folderId }));
			var elapsed = DateTime.Now - started;

			// Every generated image goes into the library so it can be reused as an input later.
			var libraryErrors = new List<string>();
			foreach (var result in Results)
			{
				if (await TryAddToLibraryAsync(result) is string error)
				{
					libraryErrors.Add(error);
				}
			}

			if (generated.Count == 0)
			{
				Status = "The workflow finished without producing an image.";
			}
			else if (libraryErrors.Count > 0)
			{
				Status = $"Done in {elapsed.TotalSeconds:0} s, but couldn't add it to the library: {libraryErrors[0]}";
			}
			else
			{
				Status = $"Done in {elapsed.TotalSeconds:0} s. Added to library.";
			}

			StatusIsError = generated.Count == 0 || libraryErrors.Count > 0;
			HasNewResults = generated.Count > 0;
		}
		catch (OperationCanceledException)
		{
			Status = "Cancelled. A job that already started may still finish on the server.";
		}
		catch (Exception ex)
		{
			Status = ex.Message;
			StatusIsError = true;
		}
		finally
		{
			Busy = false;
			cts.Dispose();
			cts = null;
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// Puts the form back as it is on first opening: no prompt, images, results or status, default options, and the
	/// output folder following Settings again. Does nothing while a generation is running.
	/// </summary>
	public void Reset(string defaultOutputFolderId)
	{
		if (Busy)
		{
			return;
		}

		Images.Clear();
		Results.Clear();
		Prompt = null;
		SizeKey = "square";
		RefinePrompt = true;
		Thinking = true;
		OutputFolderId = defaultOutputFolderId;
		OutputFolderChosen = false;
		FileName = null;
		workflowChoices.Clear();
		Status = null;
		StatusIsError = false;
		HasNewResults = false;
		Changed?.Invoke();
	}

	public void Cancel()
	{
		cts?.Cancel();
	}

	/// <summary>Adds a result to the library. Returns the error message if it fails.</summary>
	public async Task<string?> TryAddToLibraryAsync(ResultItem result)
	{
		result.SavingToLibrary = true;
		try
		{
			var thumbnail = await thumbnailService.CreateAsync(result.Image.Data, "image/png");
			await libraryService.AddAsync(Path.GetFileName(result.Image.FilePath), result.Image.Data, thumbnail,
				result.LibraryFolderId, new LibraryImageMetadata(LibraryImageSource.Generated, result.Image.Prompt, Workflow: result.Image.Workflow));
			result.InLibrary = true;
			return null;
		}
		catch (Exception ex)
		{
			return ex.Message;
		}
		finally
		{
			result.SavingToLibrary = false;
		}
	}

	/// <param name="DataUrl">What the tile shows: the image itself, or a library thumbnail.</param>
	public sealed record InputItem(InputImage Input, string DataUrl)
	{
		/// <summary>
		/// A PNG mask drawn over this image, white where it should change. Only used while the image is image 1.
		/// </summary>
		public byte[]? Mask { get; init; }

		/// <summary>The mask as a small red see-through overlay for the tile, when there is one.</summary>
		public string? MaskOverlayUrl { get; init; }

		public static InputItem FromData(InputImage input, string? contentType)
		{
			return new InputItem(input, $"data:{(string.IsNullOrEmpty(contentType) ? "image/png" : contentType)};base64,{Convert.ToBase64String(input.Data)}");
		}
	}

	public sealed record ResultItem(GeneratedImage Image, string DataUrl)
	{
		/// <summary>The library folder chosen when it was generated: null for the top of the library.</summary>
		public string? LibraryFolderId { get; init; }

		public bool InLibrary { get; set; }

		public bool SavingToLibrary { get; set; }
	}
}
