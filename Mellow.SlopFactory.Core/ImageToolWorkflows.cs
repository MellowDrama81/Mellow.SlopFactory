using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Builds the one-image tool workflows: background removal (Workflows/birefnet_remove_background_api.json) and
    /// upscaling (Workflows/seedvr2_upscale_api.json), both embedded.
    /// </summary>
    public static class ImageToolWorkflows
    {
        private const string LoadImageNode = "load_image";
        private const string ResizeNode = "resize";
        private const string SamplerNode = "sampler";

        /// <summary>Scale factors offered for upscaling. SeedVR2's template uses 4.</summary>
        public static IReadOnlyList<int> UpscaleFactors { get; } = [2, 4];

        /// <summary>
        /// Cuts the subject out with BiRefNet. The output is the input with an alpha channel: the subject's pixels are
        /// unchanged.
        /// </summary>
        /// <param name="image">Name of an image already uploaded to ComfyUI's input folder.</param>
        public static JsonObject RemoveBackground(string image)
        {
            var workflow = EmbeddedWorkflows.Load(EmbeddedWorkflows.RemoveBackground);
            Inputs(workflow, LoadImageNode)["image"] = image;
            return workflow;
        }

        /// <summary>
        /// Upscales with SeedVR2 7B (one step), which restores detail rather than just resizing. Any alpha channel is
        /// kept.
        /// </summary>
        /// <param name="image">Name of an image already uploaded to ComfyUI's input folder.</param>
        /// <param name="factor">How many times larger each side gets, 1 to 8.</param>
        public static JsonObject Upscale(string image, double factor = 4, long? seed = null)
        {
            if (factor is < 1 or > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "Upscaling takes a factor from 1 to 8.");
            }

            var workflow = EmbeddedWorkflows.Load(EmbeddedWorkflows.Upscale);
            Inputs(workflow, LoadImageNode)["image"] = image;
            Inputs(workflow, ResizeNode)["resize_type.multiplier"] = factor;
            Inputs(workflow, SamplerNode)["seed"] = seed ?? Random.Shared.NextInt64(0, 1L << 50);
            return workflow;
        }

        private static JsonObject Inputs(JsonObject workflow, string nodeId)
        {
            return workflow[nodeId]?["inputs"] as JsonObject
                ?? throw new ArgumentException($"Workflow is missing node {nodeId}.");
        }
    }
}
