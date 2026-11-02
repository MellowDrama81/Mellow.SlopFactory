using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the Qwen Image 2.1 inpaint API workflow (Workflows/qwen_image_2_1_inpaint_api.json, embedded):
    /// the edit workflow, limited to a masked area of image_1.
    /// </summary>
    public class QwenImageInpaintOptions
    {
        public const int MinImages = 1;
        public const int MaxImages = 10;

        /// <summary>What to do in the masked area. Refer to inputs as &lt;image1&gt; ... &lt;image10&gt;.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Names of images already uploaded to ComfyUI's input folder (see ComfyApi.UploadImageAsync).
        /// image_1 is the image being inpainted; the rest are references. 1 to 10 images.
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

        /// <summary>Rewrite the prompt with the Qwen 3.5 prompt enhancer first.</summary>
        public bool RefinePrompt { get; set; }

        /// <summary>Let the prompt enhancer reason before rewriting. Only used when RefinePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>Token budget for the enhancer; see <see cref="QwenImageEditOptions.MaxLength"/>.</summary>
        public int MaxLength { get; set; } = 8192;

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        public int Steps { get; set; } = 25;

        /// <summary>Total pixel budget reference images are resized to (e.g. 1024). 0 keeps their own size.</summary>
        public int Resolution { get; set; }
    }

    public static class QwenImageInpaintWorkflow
    {
        private const string PromptNode = "prompt";
        private const string EncodeNode = "474";
        private const string BatchNode = "485";
        private const string EnhancerNode = "500";
        private const string RefineSwitchNode = "484";
        private const string MaskNode = "mask";
        private const string MaskGrowNode = "mask_grow";
        private const string FeatherNode = "feather_blur";
        private const string PasteBackNode = "paste_back";
        private const string SamplerNode = "458";

        /// <summary>
        /// Whether a workflow is the Qwen Image 2.1 inpaint template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow[EncodeNode]?["class_type"]?.GetValue<string>() == "TextEncodeQwenImage21"
                && workflow[MaskNode]?["class_type"]?.GetValue<string>() == "LoadImageMask"
                && workflow["noise_mask"]?["class_type"]?.GetValue<string>() == "SetLatentNoiseMask";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.QwenImageInpaint);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(QwenImageInpaintOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, QwenImageInpaintOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the Qwen Image 2.1 inpaint template.", nameof(template));
            }

            if (options.Images.Count is < QwenImageInpaintOptions.MinImages or > QwenImageInpaintOptions.MaxImages)
            {
                throw new ArgumentException(
                    $"Qwen Image 2.1 inpaint needs {QwenImageInpaintOptions.MinImages} to {QwenImageInpaintOptions.MaxImages} input images (got {options.Images.Count}).",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.Mask))
            {
                throw new ArgumentException("Qwen Image 2.1 inpaint needs a mask.", nameof(options));
            }

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = options.Prompt;

            var encodeInputs = Inputs(workflow, EncodeNode);
            var batchInputs = Inputs(workflow, BatchNode);
            encodeInputs["resolution"] = options.Resolution;

            // The template already refers to load_image_1 for the image being inpainted.
            for (var i = 0; i < options.Images.Count; i++)
            {
                var loadNode = $"load_image_{i + 1}";
                workflow[loadNode] = new JsonObject
                {
                    ["class_type"] = "LoadImage",
                    ["inputs"] = new JsonObject { ["image"] = options.Images[i] },
                    ["_meta"] = new JsonObject { ["title"] = $"image_{i + 1}" },
                };

                encodeInputs[$"images.image_{i + 1}"] = new JsonArray(loadNode, 0);
                batchInputs[$"images.image{i}"] = new JsonArray(loadNode, 0);
            }

            Inputs(workflow, MaskNode)["image"] = options.Mask;
            Inputs(workflow, MaskGrowNode)["expand"] = options.MaskGrow;
            Inputs(workflow, FeatherNode)["blur_radius"] = Math.Clamp(options.Feather, 1, 31);
            Inputs(workflow, PasteBackNode)["switch"] = options.PasteBack;

            var enhancerInputs = Inputs(workflow, EnhancerNode);
            Inputs(workflow, RefineSwitchNode)["switch"] = options.RefinePrompt;
            enhancerInputs["thinking"] = options.Thinking;
            enhancerInputs["max_length"] = options.MaxLength;
            enhancerInputs["sampling_mode.seed"] = options.Seed;

            var samplerInputs = Inputs(workflow, SamplerNode);
            samplerInputs["seed"] = options.Seed;
            samplerInputs["steps"] = options.Steps;

            return workflow;
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
