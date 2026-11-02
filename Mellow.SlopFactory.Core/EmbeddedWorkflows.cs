using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// API-format workflows compiled into this assembly from the Workflows folder.
    /// </summary>
    public static class EmbeddedWorkflows
    {
        public const string QwenImageEdit = "qwen_image_2_1_edit_api.json";
        public const string QwenImageGenerate = "qwen_image_2_1_t2i_api.json";
        public const string QwenImageInpaint = "qwen_image_2_1_inpaint_api.json";
        public const string Flux3KleinGenerate = "flux3_image_klein_t2i_api.json";
        public const string Flux3KleinEdit = "flux3_image_klein_edit_api.json";
        public const string Flux3KleinInpaint = "flux3_image_klein_inpaint_api.json";
        public const string Krea2TurboGenerate = "krea2_turbo_t2i_api.json";
        public const string ZImageTurboGenerate = "z_image_turbo_t2i_api.json";
        public const string AnimaGenerate = "anima_t2i_api.json";
        public const string NewBieGenerate = "newbie_t2i_api.json";
        public const string NetaYumeLuminaGenerate = "netayume_lumina_t2i_api.json";
        public const string RemoveBackground = "birefnet_remove_background_api.json";
        public const string Upscale = "seedvr2_upscale_api.json";

        private const string ResourcePrefix = "Mellow.SlopFactory.Core.Workflows.";

        /// <summary>File names of every embedded workflow.</summary>
        public static IReadOnlyList<string> Names { get; } = typeof(EmbeddedWorkflows).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .ToList();

        /// <summary>
        /// Loads a fresh copy of an embedded workflow by file name (e.g. EmbeddedWorkflows.QwenImageEdit).
        /// </summary>
        public static JsonObject Load(string name)
        {
            using var stream = typeof(EmbeddedWorkflows).Assembly.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new ArgumentException($"No embedded workflow named '{name}'. Available: {string.Join(", ", Names)}.", nameof(name));

            return JsonNode.Parse(stream) as JsonObject
                ?? throw new InvalidOperationException($"Embedded workflow '{name}' is not a JSON object.");
        }
    }
}
