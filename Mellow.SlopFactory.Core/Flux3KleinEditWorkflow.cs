using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Options for the FLUX 3 Image Klein image-edit API workflow (Workflows/flux3_image_klein_edit_api.json, embedded).
    /// </summary>
    public class Flux3KleinEditOptions
    {
        public const int MinImages = 1;

        /// <summary>
        /// The node takes up to 100; 31 ran fine on Comfy Cloud (in about three minutes, with a few references dropped),
        /// so this allows a little past what's been tried. About 20 is the realistic limit for reliable results.
        /// </summary>
        public const int MaxImages = 32;

        /// <summary>
        /// The edit. Refer to inputs as &lt;image1&gt; ... &lt;imageN&gt;; they're translated into the forms the upsampler
        /// ("the second image") and the model (&lt;ref_image_1&gt;) expect.
        /// </summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Names of images already uploaded to ComfyUI's input folder (see ComfyApi.UploadImageAsync). image_1 is the
        /// canvas and sets the output size; the rest are references.
        /// </summary>
        public IReadOnlyList<string> Images { get; set; } = [];

        /// <summary>Expand the prompt with BFL's prompt upsampler, which looks at every image.</summary>
        public bool UpsamplePrompt { get; set; } = true;

        /// <summary>Let the upsampler reason first. Only used when UpsamplePrompt is on.</summary>
        public bool Thinking { get; set; }

        /// <summary>Token budget for the upsampler. Null uses the template's 3584, or 8192 with thinking.</summary>
        public int? MaxLength { get; set; }

        public long Seed { get; set; } = Random.Shared.NextInt64(0, 1L << 50);

        /// <summary>The template's settings: 50 steps at CFG 3.5.</summary>
        public int Steps { get; set; } = 50;
        public double Cfg { get; set; } = 3.5;

        /// <summary>Size image_1 is encoded at, in megapixels. The output takes this size.</summary>
        public double CanvasMegapixels { get; set; } = 1.0;

        /// <summary>
        /// Size each further reference is encoded at, in megapixels. Null fits them into a budget so many references
        /// stay affordable (see <see cref="Flux3KleinEditWorkflow.ReferenceMegapixelsFor"/>).
        /// </summary>
        public double? ReferenceMegapixels { get; set; }
    }

    public static class Flux3KleinEditWorkflow
    {
        private const string PromptNode = "prompt";
        private const string DirectPromptNode = "prompt_direct";
        private const string BatchNode = "batch";
        private const string UpsamplerNode = "upsampler";
        private const string RefineSwitchNode = "refine_switch";
        private const string RefsNode = "refs";
        private const string MoreRefsNode = "refs_more";
        private const string SamplerNode = "sampler";

        private static readonly Regex ImageTag = new(@"<\s*image\s*(\d+)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] Ordinals =
            ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth"];

        /// <summary>
        /// Default size for references after image_1: 1 MP each for a few, then shrinking so the total stays near
        /// <paramref name="budget"/> MP, but never below 0.25 MP (512 x 512).
        /// </summary>
        public static double ReferenceMegapixelsFor(int referenceCount, double budget = 4.0)
        {
            return referenceCount <= 0 ? 1.0 : Math.Round(Math.Clamp(budget / referenceCount, 0.25, 1.0), 2);
        }

        /// <summary>
        /// Whether a workflow is the FLUX 3 Image Klein edit template this class knows how to fill in.
        /// </summary>
        public static bool IsMatch(JsonObject workflow)
        {
            return workflow["unet"]?["inputs"]?["unet_name"]?.GetValue<string>()?.StartsWith("flux3-image-klein", StringComparison.Ordinal) == true
                && workflow[RefsNode]?["class_type"]?.GetValue<string>() == "ReferenceMultiLatent"
                && workflow[DirectPromptNode]?["class_type"]?.GetValue<string>() == "PrimitiveStringMultiline";
        }

        /// <summary>
        /// Loads the embedded template, unfilled.
        /// </summary>
        public static JsonObject LoadTemplate()
        {
            return EmbeddedWorkflows.Load(EmbeddedWorkflows.Flux3KleinEdit);
        }

        /// <summary>
        /// Builds the workflow from the embedded template.
        /// </summary>
        public static JsonObject Build(Flux3KleinEditOptions options)
        {
            return Build(LoadTemplate(), options);
        }

        /// <summary>
        /// Returns a copy of the template with the options applied.
        /// </summary>
        public static JsonObject Build(JsonObject template, Flux3KleinEditOptions options)
        {
            if (!IsMatch(template))
            {
                throw new ArgumentException("Workflow is not the FLUX 3 Image Klein edit template.", nameof(template));
            }

            if (options.Images.Count is < Flux3KleinEditOptions.MinImages or > Flux3KleinEditOptions.MaxImages)
            {
                throw new ArgumentException(
                    $"FLUX 3 Image Klein edit needs {Flux3KleinEditOptions.MinImages} to {Flux3KleinEditOptions.MaxImages} input images (got {options.Images.Count}).",
                    nameof(options));
            }

            var workflow = (JsonObject)template.DeepClone();
            Inputs(workflow, PromptNode)["value"] = ToOrdinals(options.Prompt);
            Inputs(workflow, DirectPromptNode)["value"] = ToReferenceTags(options.Prompt);

            var batchInputs = Inputs(workflow, BatchNode);
            var refsInputs = Inputs(workflow, RefsNode);
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

            refsInputs["resolution.megapixels"] = options.CanvasMegapixels;
            var samplerInputs = Inputs(workflow, SamplerNode);
            if (options.Images.Count == 1)
            {
                // Only the canvas: no further references, so skip the second reference node.
                workflow.Remove(MoreRefsNode);
                samplerInputs["positive"] = new JsonArray(RefsNode, 0);
                samplerInputs["negative"] = new JsonArray(RefsNode, 1);
            }
            else
            {
                moreRefsInputs["resolution.megapixels"] = options.ReferenceMegapixels ?? ReferenceMegapixelsFor(options.Images.Count - 1);
            }

            Inputs(workflow, RefineSwitchNode)["switch"] = options.UpsamplePrompt;
            var upsamplerInputs = Inputs(workflow, UpsamplerNode);
            upsamplerInputs["thinking"] = options.Thinking;
            upsamplerInputs["max_length"] = options.MaxLength ?? (options.Thinking ? 8192 : 3584);

            samplerInputs["seed"] = options.Seed;
            samplerInputs["steps"] = options.Steps;
            samplerInputs["cfg"] = options.Cfg;

            return workflow;
        }

        /// <summary>"&lt;image2&gt;" becomes "&lt;ref_image_1&gt;", the model's own tag (counted from 0).</summary>
        public static string ToReferenceTags(string prompt)
        {
            return ImageTag.Replace(prompt, m => $"<ref_image_{int.Parse(m.Groups[1].Value) - 1}>");
        }

        /// <summary>"&lt;image2&gt;" becomes "the second image", as the upsampler's system prompt refers to them.</summary>
        public static string ToOrdinals(string prompt)
        {
            return ImageTag.Replace(prompt, m =>
            {
                var n = int.Parse(m.Groups[1].Value);
                return n >= 1 && n <= Ordinals.Length ? $"the {Ordinals[n - 1]} image" : $"image {n}";
            });
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
