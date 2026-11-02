using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the Qwen Image 2.1 text-to-image API workflow (Workflows/qwen_image_2_1_t2i_api.json, embedded).
    /// </summary>
    public class QwenImageGenerateOptions
    {
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Rewrite the prompt with the Qwen 3.5 text-to-image prompt enhancer before generating.</summary>
        public bool RefinePrompt { get; set; }

        /// <summary>Let the prompt enhancer reason before rewriting. Only used when RefinePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>
        /// Token budget for the enhancer's reasoning plus the rewritten prompt. Budgets that are too low
        /// leave nothing for the rewrite (the workflow then falls back to the original prompt).
        /// The official setting is 16256.
        /// </summary>
        public int MaxLength { get; set; } = 8192;

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        public int Steps { get; set; } = 25;

        /// <summary>Output size. Qwen Image 2.1 supports up to 2048x2048; prefer multiples of 32.</summary>
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
    }

    public static class QwenImageGenerateWorkflow
    {
        private const string PromptNode = "prompt";
        private const string EncodeNode = "452";
        private const string EnhancerNode = "471";
        private const string RefineSwitchNode = "474";
        private const string EmptyLatentNode = "456";
        private const string SamplerNode = "458";

        /// <summary>
        /// Whether a workflow is the Qwen Image 2.1 text-to-image template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow[EncodeNode]?["class_type"]?.GetValue<string>() == "TextEncodeQwenImage21"
                && workflow[EnhancerNode]?["class_type"]?.GetValue<string>() == "TextGenerate"
                && workflow[PromptNode]?["class_type"]?.GetValue<string>() == "PrimitiveStringMultiline";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.QwenImageGenerate);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(QwenImageGenerateOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, QwenImageGenerateOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the Qwen Image 2.1 text-to-image template.", nameof(template));
            }

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = options.Prompt;

            Inputs(workflow, RefineSwitchNode)["switch"] = options.RefinePrompt;
            var enhancerInputs = Inputs(workflow, EnhancerNode);
            enhancerInputs["thinking"] = options.Thinking;
            enhancerInputs["max_length"] = options.MaxLength;
            enhancerInputs["sampling_mode.seed"] = options.Seed;

            var latentInputs = Inputs(workflow, EmptyLatentNode);
            latentInputs["width"] = options.Width;
            latentInputs["height"] = options.Height;

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
