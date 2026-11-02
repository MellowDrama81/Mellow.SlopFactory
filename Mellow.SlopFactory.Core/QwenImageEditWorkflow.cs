using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the Qwen Image 2.1 edit API workflow (Workflows/qwen_image_2_1_edit_api.json, embedded).
    /// </summary>
    public class QwenImageEditOptions
    {
        public const int MinImages = 1;
        public const int MaxImages = 10;

        /// <summary>Edit instruction. Refer to inputs as &lt;image1&gt; ... &lt;image10&gt;.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Names of images already uploaded to ComfyUI's input folder (see ComfyApi.UploadImageAsync).
        /// image_1 is the edit target; the rest are references. 1 to 10 images.
        /// </summary>
        public IReadOnlyList<string> Images { get; set; } = [];

        /// <summary>Rewrite the prompt with the Qwen 3.5 prompt enhancer before editing.</summary>
        public bool RefinePrompt { get; set; }

        /// <summary>Let the prompt enhancer reason before rewriting. Only used when RefinePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>
        /// Token budget for the enhancer's reasoning plus the rewritten prompt. The enhancer reasons for
        /// roughly 4000 tokens, so much lower budgets can leave nothing for the rewrite (the workflow then
        /// falls back to the original prompt). The official setting is 24000.
        /// </summary>
        public int MaxLength { get; set; } = 8192;

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        public int Steps { get; set; } = 25;

        /// <summary>
        /// Output size. When null, the canvas follows image_1.
        /// </summary>
        public int? Width { get; set; }
        public int? Height { get; set; }

        /// <summary>Total pixel budget reference images are resized to (e.g. 1024). 0 keeps their own size.</summary>
        public int Resolution { get; set; }
    }

    public static class QwenImageEditWorkflow
    {
        private const string PromptNode = "prompt";
        private const string EncodeNode = "474";
        private const string BatchNode = "485";
        private const string EnhancerNode = "500";
        private const string RefineSwitchNode = "484";
        private const string SizeSwitchNode = "468";
        private const string EmptyLatentNode = "456";
        private const string SamplerNode = "458";

        /// <summary>
        /// Whether a workflow is the Qwen Image 2.1 edit template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow[EncodeNode]?["class_type"]?.GetValue<string>() == "TextEncodeQwenImage21"
                && workflow[PromptNode]?["class_type"]?.GetValue<string>() == "PrimitiveStringMultiline";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.QwenImageEdit);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(QwenImageEditOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, QwenImageEditOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the Qwen Image 2.1 edit template.", nameof(template));
            }

            if (options.Images.Count is < QwenImageEditOptions.MinImages or > QwenImageEditOptions.MaxImages)
            {
                throw new ArgumentException(
                    $"Qwen Image 2.1 edit needs {QwenImageEditOptions.MinImages} to {QwenImageEditOptions.MaxImages} input images (got {options.Images.Count}).",
                    nameof(options));
            }

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = options.Prompt;

            var encodeInputs = Inputs(workflow, EncodeNode);
            var batchInputs = Inputs(workflow, BatchNode);
            encodeInputs["resolution"] = options.Resolution;

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

            var enhancerInputs = Inputs(workflow, EnhancerNode);
            Inputs(workflow, RefineSwitchNode)["switch"] = options.RefinePrompt;
            enhancerInputs["thinking"] = options.Thinking;
            enhancerInputs["max_length"] = options.MaxLength;
            enhancerInputs["sampling_mode.seed"] = options.Seed;

            Inputs(workflow, SizeSwitchNode)["switch"] = options.Width.HasValue || options.Height.HasValue;
            var latentInputs = Inputs(workflow, EmptyLatentNode);
            latentInputs["width"] = options.Width ?? options.Height ?? 1024;
            latentInputs["height"] = options.Height ?? options.Width ?? 1024;

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
