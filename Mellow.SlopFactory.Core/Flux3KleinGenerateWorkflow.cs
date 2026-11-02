using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the FLUX 3 Image Klein text-to-image API workflow (Workflows/flux3_image_klein_t2i_api.json, embedded).
    /// </summary>
    public class Flux3KleinGenerateOptions
    {
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Expand the prompt with BFL's Qwen 3.5 prompt upsampler first, as the template does by default.</summary>
        public bool UpsamplePrompt { get; set; } = true;

        /// <summary>Let the upsampler reason first. Only used when UpsamplePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>
        /// Token budget for the upsampler. Null uses the template's 3584, or 8192 with thinking so the reasoning
        /// leaves room for the caption (the workflow falls back to the original prompt if it doesn't).
        /// </summary>
        public int? MaxLength { get; set; }

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        /// <summary>The template's base-model settings: 50 steps at CFG 3.5.</summary>
        public int Steps { get; set; } = 50;
        public double Cfg { get; set; } = 3.5;

        /// <summary>Output size, rounded to multiples of 16.</summary>
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
    }

    public static class Flux3KleinGenerateWorkflow
    {
        private const string PromptNode = "prompt";
        private const string CanvasNode = "canvas";
        private const string UpsamplerNode = "upsampler";
        private const string RefineSwitchNode = "refine_switch";
        private const string LatentNode = "latent";
        private const string SamplerNode = "sampler";

        /// <summary>
        /// Whether a workflow is the FLUX 3 Image Klein template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow["unet"]?["inputs"]?["unet_name"]?.GetValue<string>()?.StartsWith("flux3-image-klein", StringComparison.Ordinal) == true
                && workflow[PromptNode]?["class_type"]?.GetValue<string>() == "PrimitiveStringMultiline"
                && workflow[LatentNode]?["class_type"]?.GetValue<string>() == "EmptyFlux2LatentImage";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.Flux3KleinGenerate);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(Flux3KleinGenerateOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, Flux3KleinGenerateOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the FLUX 3 Image Klein template.", nameof(template));
            }

            var width = RoundTo16(options.Width);
            var height = RoundTo16(options.Height);

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = options.Prompt;
            Inputs(workflow, CanvasNode)["value"] = $"TARGET CANVAS: {AspectRatio(width, height)}";

            Inputs(workflow, RefineSwitchNode)["switch"] = options.UpsamplePrompt;
            var upsamplerInputs = Inputs(workflow, UpsamplerNode);
            upsamplerInputs["thinking"] = options.Thinking;
            upsamplerInputs["max_length"] = options.MaxLength ?? (options.Thinking ? 8192 : 3584);

            var latentInputs = Inputs(workflow, LatentNode);
            latentInputs["width"] = width;
            latentInputs["height"] = height;

            var samplerInputs = Inputs(workflow, SamplerNode);
            samplerInputs["seed"] = options.Seed;
            samplerInputs["steps"] = options.Steps;
            samplerInputs["cfg"] = options.Cfg;

            return workflow;
        }

        private static int RoundTo16(int value)
        {
            return Math.Max(16, (int)Math.Round(value / 16.0) * 16);
        }

        /// <summary>The ratio in lowest terms, e.g. 1536 x 1024 is "3:2".</summary>
        private static string AspectRatio(int width, int height)
        {
            static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
            var gcd = Gcd(width, height);
            return $"{width / gcd}:{height / gcd}";
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
