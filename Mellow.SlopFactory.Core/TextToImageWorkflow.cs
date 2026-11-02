using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the plain text-to-image workflows <see cref="TextToImageWorkflow"/> fills in: Krea 2 Turbo, Z-Image
    /// Turbo, Anima, NewBie and NetaYume Lumina.
    /// </summary>
    public class TextToImageOptions
    {
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Rewrite the prompt with the workflow's prompt enhancer first. Ignored by workflows without one.</summary>
        public bool RefinePrompt { get; set; } = true;

        /// <summary>Let the enhancer reason first. Only used when RefinePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>Token budget for the enhancer. Null uses the template's, or 8192 with thinking.</summary>
        public int? MaxLength { get; set; }

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        /// <summary>Sampling steps and CFG. Null keeps the template's (e.g. 8 at CFG 1 for the turbo models).</summary>
        public int? Steps { get; set; }
        public double? Cfg { get; set; }

        /// <summary>Output size, rounded to multiples of 16.</summary>
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
    }

    /// <summary>
    /// Fills in the embedded text-to-image workflows that share one layout: a "prompt" string node, a "latent" empty
    /// latent and a "sampler" KSampler, plus "refine_switch" and "upsampler" nodes when the model has a prompt
    /// enhancer. NewBie also has a "tags" node; see <see cref="Build(JsonObject, TextToImageOptions)"/>.
    /// </summary>
    public static class TextToImageWorkflow
    {
        private const string PromptNode = "prompt";
        private const string TagsNode = "tags";
        private const string UpsamplerNode = "upsampler";
        private const string RefineSwitchNode = "refine_switch";
        private const string LatentNode = "latent";
        private const string SamplerNode = "sampler";

        /// <summary>Embedded workflows this class fills in.</summary>
        public static IReadOnlyList<string> Names { get; } =
        [
            EmbeddedWorkflows.Krea2TurboGenerate,
            EmbeddedWorkflows.ZImageTurboGenerate,
            EmbeddedWorkflows.AnimaGenerate,
            EmbeddedWorkflows.NewBieGenerate,
            EmbeddedWorkflows.NetaYumeLuminaGenerate,
        ];

        /// <summary>
        /// Whether a workflow has the nodes this class fills in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow[PromptNode]?["class_type"]?.GetValue<string>() == "PrimitiveStringMultiline"
                && workflow[LatentNode]?["inputs"]?["width"] != null
                && workflow[SamplerNode]?["class_type"]?.GetValue<string>() == "KSampler";
        }

        /// <summary>Whether a workflow has a prompt enhancer that <see cref="TextToImageOptions.RefinePrompt"/> turns on.</summary>
        public static bool HasPromptEnhancer(JsonObject workflow)
        {
            return workflow.ContainsKey(RefineSwitchNode) && workflow.ContainsKey(UpsamplerNode);
        }

        /// <summary>
        /// Builds one of the embedded workflows in <see cref="Names"/>.
        /// </summary>
        public static JsonObject Build(string name, TextToImageOptions options)
        {
            return Build(EmbeddedWorkflows.Load(name), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied. For NewBie, a prompt starting with "&lt;" is taken
        /// as its XML tags (e.g. &lt;character_1&gt;...) and replaces the default tags, leaving the caption empty;
        /// anything else is the caption.
        /// </summary>
        public static JsonObject Build(JsonObject template, TextToImageOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not a text-to-image template this builder knows.", nameof(template));
            }

            var workflow = (JsonObject)template.DeepClone();
            var prompt = options.Prompt.Trim();
            if (workflow.ContainsKey(TagsNode) && prompt.StartsWith('<'))
            {
                Inputs(workflow, TagsNode)["value"] = prompt;
                prompt = string.Empty;
            }

            Inputs(workflow, PromptNode)["value"] = prompt;

            if (HasPromptEnhancer(workflow))
            {
                Inputs(workflow, RefineSwitchNode)["switch"] = options.RefinePrompt;
                var upsamplerInputs = Inputs(workflow, UpsamplerNode);
                upsamplerInputs["thinking"] = options.Thinking;
                upsamplerInputs["max_length"] = options.MaxLength ?? (options.Thinking ? 8192 : upsamplerInputs["max_length"]!.GetValue<int>());
                upsamplerInputs["sampling_mode.seed"] = options.Seed;
            }

            var latentInputs = Inputs(workflow, LatentNode);
            latentInputs["width"] = RoundTo16(options.Width);
            latentInputs["height"] = RoundTo16(options.Height);

            var samplerInputs = Inputs(workflow, SamplerNode);
            samplerInputs["seed"] = options.Seed;
            if (options.Steps is { } steps)
            {
                samplerInputs["steps"] = steps;
            }

            if (options.Cfg is { } cfg)
            {
                samplerInputs["cfg"] = cfg;
            }

            return workflow;
        }

        private static int RoundTo16(int value)
        {
            return Math.Max(16, (int)Math.Round(value / 16.0) * 16);
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
