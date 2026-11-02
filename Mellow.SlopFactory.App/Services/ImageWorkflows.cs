namespace Mellow.SlopFactory.App.Services;

/// <summary>What a workflow does, which follows from the Create page's inputs.</summary>
public enum ImageWorkflowKind
{
	/// <summary>No input images: make an image from the prompt.</summary>
	Generate,

	/// <summary>Input images: change image 1, using the rest as references.</summary>
	Edit,

	/// <summary>Input images and a mask on image 1: change only the masked area.</summary>
	Inpaint,
}

/// <param name="Id">Stable id, used to remember and request the choice.</param>
/// <param name="Description">One line for under the picker.</param>
/// <param name="MaxImages">The most input images it takes; 0 for text-to-image.</param>
public record ImageWorkflow(string Id, string Name, string Description, ImageWorkflowKind Kind, int MaxImages = 0);

/// <summary>The built-in workflows the Create page can run.</summary>
public static class ImageWorkflows
{
	public const string QwenGenerate = "qwen-image-2.1-t2i";
	public const string Flux3KleinGenerate = "flux3-image-klein-t2i";
	public const string QwenEdit = "qwen-image-2.1-edit";
	public const string Flux3KleinEdit = "flux3-image-klein-edit";
	public const string QwenInpaint = "qwen-image-2.1-inpaint";

	/// <summary>
	/// How many images FLUX 3 Klein edits are offered with: the same 10 as Qwen. On Comfy Cloud it handled more (21
	/// images placed every object in under two minutes; 31 dropped a few and took three) and the node takes up to 100.
	/// </summary>
	public const int Flux3KleinEditMaxImages = 10;

	/// <summary>Every workflow; the first of each kind that takes the images is that kind's default.</summary>
	public static IReadOnlyList<ImageWorkflow> All { get; } =
	[
		new(QwenGenerate, "Qwen Image 2.1", "Alibaba's Qwen Image 2.1, with its prompt enhancer.", ImageWorkflowKind.Generate),
		new(Flux3KleinGenerate, "FLUX 3 Image Klein", "Black Forest Labs' FLUX 3 Klein base model (50 steps), with its prompt upsampler. Strong at lettering.", ImageWorkflowKind.Generate),
		new(QwenEdit, "Qwen Image 2.1 Edit", "Changes image 1 as the prompt describes, using any other images as references.", ImageWorkflowKind.Edit, Core.QwenImageEditOptions.MaxImages),
		new(Flux3KleinEdit, "FLUX 3 Image Klein Edit", "Black Forest Labs' FLUX 3 Klein. The output takes image 1's shape; extra images are shrunk to keep many references affordable.", ImageWorkflowKind.Edit, Flux3KleinEditMaxImages),
		new(QwenInpaint, "Qwen Image 2.1 Inpaint", "Redraws only the masked area of image 1; everything else stays exactly as it is.", ImageWorkflowKind.Inpaint, Core.QwenImageInpaintOptions.MaxImages),
	];

	/// <summary>The most input images any workflow takes, so the most the Create page accepts.</summary>
	public static int MaxImages { get; } = All.Max(w => w.MaxImages);

	public static ImageWorkflowKind KindFor(int imageCount, bool hasMask)
	{
		return imageCount == 0 ? ImageWorkflowKind.Generate : hasMask ? ImageWorkflowKind.Inpaint : ImageWorkflowKind.Edit;
	}

	/// <summary>The workflows of a kind that can take this many images.</summary>
	public static IReadOnlyList<ImageWorkflow> For(ImageWorkflowKind kind, int imageCount)
	{
		return All.Where(w => w.Kind == kind && (kind == ImageWorkflowKind.Generate || imageCount <= w.MaxImages)).ToList();
	}

	/// <summary>The workflows of a kind that can't take this many images, for saying why they're not offered.</summary>
	public static IReadOnlyList<ImageWorkflow> TooFewImagesFor(ImageWorkflowKind kind, int imageCount)
	{
		return All.Where(w => w.Kind == kind && kind != ImageWorkflowKind.Generate && imageCount > w.MaxImages).ToList();
	}

	/// <summary>
	/// The workflow with this id if it suits the kind and image count, otherwise the first that does. Null only if
	/// nothing of that kind takes that many images.
	/// </summary>
	public static ImageWorkflow? Resolve(ImageWorkflowKind kind, int imageCount, string? id)
	{
		var suitable = For(kind, imageCount);
		return suitable.FirstOrDefault(w => w.Id == id) ?? suitable.FirstOrDefault();
	}
}
