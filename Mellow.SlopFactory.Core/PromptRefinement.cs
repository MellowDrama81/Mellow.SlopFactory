using System.Text.Json.Nodes;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// What a prompt enhancer did in a finished workflow. The workflows show the prompt they sampled with in a
    /// PreviewAny node fed by a switch: on for the enhancer's output (which falls back to the original prompt when the
    /// enhancer returns nothing), off for the original prompt itself.
    /// </summary>
    /// <param name="FinalPrompt">The prompt the image was sampled with, or null if the workflow doesn't show one.</param>
    /// <param name="FellBack">
    /// The enhancer was on but the original prompt was used anyway, because it returned nothing.
    /// </param>
    public record PromptRefinement(string? FinalPrompt, bool FellBack)
    {
        /// <summary>Reads it from a built workflow and the text its nodes showed.</summary>
        public static PromptRefinement Read(JsonObject workflow, IReadOnlyDictionary<string, IReadOnlyList<string>> text)
        {
            foreach (var (nodeId, node) in workflow)
            {
                if (node?["class_type"]?.GetValue<string>() != "PreviewAny" || !text.TryGetValue(nodeId, out var lines))
                {
                    continue;
                }

                var finalPrompt = string.Join("\n", lines).Trim();
                var switchNode = Linked(workflow, node["inputs"]?["source"]);
                if (switchNode?["class_type"]?.GetValue<string>() != "ComfySwitchNode"
                    || switchNode["inputs"]?["switch"] is not JsonValue refining
                    || !refining.TryGetValue<bool>(out var on) || !on)
                {
                    return new PromptRefinement(finalPrompt, false);
                }

                var original = Linked(workflow, switchNode["inputs"]?["on_false"])?["inputs"]?["value"]?.GetValue<string>()?.Trim();
                return new PromptRefinement(finalPrompt, original != null && finalPrompt == original);
            }

            return new PromptRefinement(null, false);
        }

        /// <summary>The node a link (["node id", slot]) points at, or null if it isn't a link.</summary>
        private static JsonObject? Linked(JsonObject workflow, JsonNode? link)
        {
            return link is JsonArray { Count: 2 } pair && pair[0] is JsonValue id && id.TryGetValue<string>(out var nodeId)
                ? workflow[nodeId] as JsonObject
                : null;
        }
    }
}
