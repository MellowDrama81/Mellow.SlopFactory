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
public record ImageWorkflow(string Id, string Name, string Description, ImageWorkflowKind Kind);

/// <summary>The built-in workflows the Create page can run.</summary>
public static class ImageWorkflows
{
	public const string QwenGenerate = "qwen-image-2.1-t2i";
	public const string Flux3KleinGenerate = "flux3-image-klein-t2i";
	public const string QwenEdit = "qwen-image-2.1-edit";
	public const string QwenInpaint = "qwen-image-2.1-inpaint";

	/// <summary>Every workflow; the first of each kind is that kind's default.</summary>
	public static IReadOnlyList<ImageWorkflow> All { get; } =
	[
		new(QwenGenerate, "Qwen Image 2.1", "Alibaba's Qwen Image 2.1, with its prompt enhancer.", ImageWorkflowKind.Generate),
		new(Flux3KleinGenerate, "FLUX 3 Image Klein", "Black Forest Labs' FLUX 3 Klein base model (50 steps), with its prompt upsampler. Strong at lettering.", ImageWorkflowKind.Generate),
		new(QwenEdit, "Qwen Image 2.1 Edit", "Changes image 1 as the prompt describes, using any other images as references.", ImageWorkflowKind.Edit),
		new(QwenInpaint, "Qwen Image 2.1 Inpaint", "Redraws only the masked area of image 1; everything else stays exactly as it is.", ImageWorkflowKind.Inpaint),
	];

	public static ImageWorkflowKind KindFor(int imageCount, bool hasMask)
	{
		return imageCount == 0 ? ImageWorkflowKind.Generate : hasMask ? ImageWorkflowKind.Inpaint : ImageWorkflowKind.Edit;
	}

	public static IReadOnlyList<ImageWorkflow> For(ImageWorkflowKind kind)
	{
		return All.Where(w => w.Kind == kind).ToList();
	}

	public static ImageWorkflow Default(ImageWorkflowKind kind)
	{
		return All.First(w => w.Kind == kind);
	}

	/// <summary>The workflow with this id if it suits the kind, otherwise the kind's default.</summary>
	public static ImageWorkflow Resolve(ImageWorkflowKind kind, string? id)
	{
		return All.FirstOrDefault(w => w.Id == id && w.Kind == kind) ?? Default(kind);
	}
}
