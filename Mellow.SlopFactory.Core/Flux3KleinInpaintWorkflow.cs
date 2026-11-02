using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the FLUX 3 Image Klein inpaint API workflow (Workflows/flux3_image_klein_inpaint_api.json,
    /// embedded): the edit workflow, limited to a masked area of image_1.
    /// </summary>
    public class Flux3KleinInpaintOptions
    {
        public const int MinImages = 1;

        /// <summary>The same limit as <see cref="Flux3KleinEditOptions.MaxImages"/>.</summary>
        public const int MaxImages = Flux3KleinEditOptions.MaxImages;

        /// <summary>
        /// What to do in the masked area. Refer to inputs as &lt;image1&gt; ... &lt;imageN&gt;, as for
        /// <see cref="Flux3KleinEditOptions.Prompt"/>.
        /// </summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Names of images already uploaded to ComfyUI's input folder (see ComfyApi.UploadImageAsync). image_1 is the
        /// image being inpainted and sets the output size; the rest are references.
        /// </summary>
        public IReadOnlyList<string> Images { get; set; } = [];

        /// <summary>
        /// Name of the uploaded mask image: white where image_1 should change, black where it should stay.
        /// It's stretched to image_1's size, so it should have the same aspect ratio.
        /// </summary>
        public string Mask { get; set; } = string.Empty;

        /// <summary>Pixels to grow the mask by, so the change blends into its surroundings.</summary>
        public int MaskGrow { get; set; } = 8;

        /// <summary>Blur radius (1 to 31) for the edge of the mask used to paste the result back.</summary>
        public int Feather { get; set; } = 12;

        /// <summary>
        /// Paste the result onto the original image_1 through the feathered mask, so everything outside the mask is
        /// exactly the original. Off returns the sampler's output as is, re-encoded everywhere.
        /// </summary>
        public bool PasteBack { get; set; } = true;

        /// <summary>Expand the prompt with BFL's prompt upsampler, which looks at every image (but not the mask).</summary>
        public bool UpsamplePrompt { get; set; } = true;

        /// <summary>Let the upsampler reason first. Only used when UpsamplePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>Token budget for the upsampler. Null uses the template's 3584, or 8192 with thinking.</summary>
        public int? MaxLength { get; set; }

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        /// <summary>The edit template's settings: 50 steps at CFG 3.5.</summary>
        public int Steps { get; set; } = 50;
        public double Cfg { get; set; } = 3.5;

        /// <summary>Size image_1 is encoded and redrawn at, in megapixels, before being pasted back at its own size.</summary>
        public double CanvasMegapixels { get; set; } = 1.0;

        /// <summary>
        /// Size each further reference is encoded at, in megapixels. Null fits them into a budget, as for edits (see
        /// <see cref="Flux3KleinEditWorkflow.ReferenceMegapixelsFor"/>).
        /// </summary>
        public double? ReferenceMegapixels { get; set; }
    }

    public static class Flux3KleinInpaintWorkflow
    {
        private const string PromptNode = "prompt";
        private const string DirectPromptNode = "prompt_direct";
        private const string BatchNode = "batch";
        private const string UpsamplerNode = "upsampler";
        private const string RefineSwitchNode = "refine_switch";
        private const string RefsNode = "refs";
        private const string MoreRefsNode = "refs_more";
        private const string MaskNode = "mask";
        private const string MaskGrowNode = "mask_grow";
        private const string FeatherNode = "feather_blur";
        private const string PasteBackNode = "paste_back";
        private const string SamplerNode = "sampler";

        /// <summary>
        /// Whether a workflow is the FLUX 3 Image Klein inpaint template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return Flux3KleinEditWorkflow.IsMatch(workflow)
                && workflow[MaskNode]?["class_type"]?.GetValue<string>() == "LoadImageMask"
                && workflow["noise_mask"]?["class_type"]?.GetValue<string>() == "SetLatentNoiseMask";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.Flux3KleinInpaint);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(Flux3KleinInpaintOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, Flux3KleinInpaintOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the FLUX 3 Image Klein inpaint template.", nameof(template));
            }

            if (options.Images.Count is < Flux3KleinInpaintOptions.MinImages or > Flux3KleinInpaintOptions.MaxImages)
            {
                throw new ArgumentException(
                    $"FLUX 3 Image Klein inpaint needs {Flux3KleinInpaintOptions.MinImages} to {Flux3KleinInpaintOptions.MaxImages} input images (got {options.Images.Count}).",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.Mask))
            {
                throw new ArgumentException("FLUX 3 Image Klein inpaint needs a mask.", nameof(options));
            }

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = Flux3KleinEditWorkflow.ToOrdinals(options.Prompt);
            Inputs(workflow, DirectPromptNode)["value"] = Flux3KleinEditWorkflow.ToReferenceTags(options.Prompt);

            // The template already refers to load_image_1 for the image being inpainted.
            var batchInputs = Inputs(workflow, BatchNode);
            var moreRefsInputs = Inputs(workflow, MoreRefsNode);
            for (var i = 0; i < options.Images.Count; i++)
            {
                var loadNode = $"load_image_{i + 1}";
                workflow[loadNode] = new JsonObject
                {
                    ["class_type"] = "LoadImage",
                    ["inputs"] = new JsonObject { ["image"] = options.Images[i] },
                    ["_meta"] = new JsonObject { ["title"] = $"image_{i + 1}" },
                };

                batchInputs[$"images.image{i}"] = new JsonArray(loadNode, 0);
                if (i > 0)
                {
                    moreRefsInputs[$"images.image{i - 1}"] = new JsonArray(loadNode, 0);
                }
            }

            Inputs(workflow, RefsNode)["resolution.megapixels"] = options.CanvasMegapixels;
            var samplerInputs = Inputs(workflow, SamplerNode);
            if (options.Images.Count == 1)
            {
                // Only the image being inpainted: no further references, so skip the second reference node.
                workflow.Remove(MoreRefsNode);
                samplerInputs["positive"] = new JsonArray(RefsNode, 0);
                samplerInputs["negative"] = new JsonArray(RefsNode, 1);
            }
            else
            {
                moreRefsInputs["resolution.megapixels"] = options.ReferenceMegapixels ?? Flux3KleinEditWorkflow.ReferenceMegapixelsFor(options.Images.Count - 1);
            }

            Inputs(workflow, MaskNode)["image"] = options.Mask;
            Inputs(workflow, MaskGrowNode)["expand"] = options.MaskGrow;
            Inputs(workflow, FeatherNode)["blur_radius"] = Math.Clamp(options.Feather, 1, 31);
            Inputs(workflow, PasteBackNode)["switch"] = options.PasteBack;

            Inputs(workflow, RefineSwitchNode)["switch"] = options.UpsamplePrompt;
            var upsamplerInputs = Inputs(workflow, UpsamplerNode);
            upsamplerInputs["thinking"] = options.Thinking;
            upsamplerInputs["max_length"] = options.MaxLength ?? (options.Thinking ? 8192 : 3584);

            samplerInputs["seed"] = options.Seed;
            samplerInputs["steps"] = options.Steps;
            samplerInputs["cfg"] = options.Cfg;

            return workflow;
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
