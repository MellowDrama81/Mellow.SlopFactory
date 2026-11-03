using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mellow.SlopFactory.Core
{
    /// <summary>
    /// Thin wrapper around the ComfyUI HTTP API (/api/prompt, /api/history, /api/view, ...).
    /// Works with both self-hosted ComfyUI and Comfy Cloud (https://cloud.comfy.org).
    /// Workflows must be in the "API format" exported from ComfyUI (Workflow > Export (API)).
    /// </summary>
    public class ComfyApi : IDisposable
    {
        private const string DefaultBaseUrl = "http://127.0.0.1:8188";

        // Comfy Cloud job states that mean the job will make no further progress.
        private static readonly HashSet<string> CloudTerminalStates = ["success", "error", "non_retryable_error", "lost", "cancelled"];

        // Used to follow Comfy Cloud's /api/view redirects to signed storage URLs without sending our API key.
        private static readonly HttpClient DownloadClient = new();

        private readonly ComfyApiSettings settings;
        private readonly HttpClient httpClient;
        private readonly bool ownsHttpClient;
        private readonly bool isCloud;
        private readonly string clientId = Guid.NewGuid().ToString("N");

        /// <summary>The server's address, e.g. "https://cloud.comfy.org/".</summary>
        public Uri BaseAddress => httpClient.BaseAddress!;

        public ComfyApi(ComfyApiSettings settings)
            : this(settings, null)
        {
        }

        public ComfyApi(ComfyApiSettings settings, HttpClient? httpClient)
        {
            this.settings = settings;
            this.ownsHttpClient = httpClient == null;
            this.httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

            var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultBaseUrl : settings.BaseUrl;
            this.httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            this.isCloud = settings.IsCloud ?? this.httpClient.BaseAddress.Host.Equals("cloud.comfy.org", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                if (isCloud)
                {
                    this.httpClient.DefaultRequestHeaders.Add("X-API-Key", settings.ApiKey);
                }
                else
                {
                    this.httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                }
            }
        }

        /// <summary>
        /// Queues an API-format workflow for execution and returns the prompt id.
        /// </summary>
        public async Task<ComfyQueueResult> QueuePromptAsync(JsonNode workflow, CancellationToken cancellationToken = default)
        {
            var body = new JsonObject
            {
                ["prompt"] = workflow.DeepClone(),
                ["client_id"] = clientId,
            };

            if (isCloud && !string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                // Partner (paid API) nodes on Comfy Cloud read the key from extra_data.
                body["extra_data"] = new JsonObject { ["api_key_comfy_org"] = settings.ApiKey };
            }

            using var response = await httpClient.PostAsJsonAsync("api/prompt", body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ComfyApiException($"ComfyUI rejected the prompt ({(int)response.StatusCode}): {error}");
            }

            var result = await response.Content.ReadFromJsonAsync<ComfyQueueResult>(cancellationToken);
            return result ?? throw new ComfyApiException("ComfyUI returned an empty response when queueing the prompt.");
        }

        /// <summary>
        /// Gets the history entry for a prompt, or null if it hasn't finished (or doesn't exist).
        /// </summary>
        public async Task<ComfyHistoryEntry?> GetHistoryAsync(string promptId, CancellationToken cancellationToken = default)
        {
            // Comfy Cloud only serves history from /api/history_v2, and returns 404 until the job has finished.
            var path = isCloud ? "api/history_v2/" : "api/history/";
            using var response = await httpClient.GetAsync(path + Uri.EscapeDataString(promptId), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, "fetching history", cancellationToken);

            var history = await response.Content.ReadFromJsonAsync<Dictionary<string, ComfyHistoryEntry>>(cancellationToken);
            return history != null && history.TryGetValue(promptId, out var entry) ? entry : null;
        }

        /// <summary>
        /// Gets a Comfy Cloud job's state (e.g. queued_waiting, executing, success, error).
        /// Only available on Comfy Cloud.
        /// </summary>
        public async Task<string?> GetCloudJobStatusAsync(string promptId, CancellationToken cancellationToken = default)
        {
            using var response = await httpClient.GetAsync($"api/job/{Uri.EscapeDataString(promptId)}/status", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, "fetching job status", cancellationToken);

            var status = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            return status?["status"]?.GetValue<string>();
        }

        /// <summary>
        /// Polls until the prompt has completed (or errored).
        /// </summary>
        public async Task<ComfyHistoryEntry> WaitForCompletionAsync(string promptId, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
        {
            var interval = pollInterval ?? TimeSpan.FromSeconds(1);

            if (isCloud)
            {
                // Cloud jobs that are lost or cancelled never get a history entry, so wait on the job state first.
                while (true)
                {
                    var state = await GetCloudJobStatusAsync(promptId, cancellationToken);
                    if (state != null && CloudTerminalStates.Contains(state))
                    {
                        if (state != "success")
                        {
                            var details = (await GetHistoryAsync(promptId, cancellationToken))?.Status?.Messages?.ToJsonString();
                            throw new ComfyApiException($"Comfy Cloud job {promptId} ended with status '{state}'. {details}".TrimEnd());
                        }

                        break;
                    }

                    await Task.Delay(interval, cancellationToken);
                }
            }

            while (true)
            {
                var entry = await GetHistoryAsync(promptId, cancellationToken);
                if (entry?.Status != null)
                {
                    if (entry.Status.StatusStr == "error")
                    {
                        throw new ComfyApiException($"ComfyUI prompt {promptId} failed: {entry.Status.Messages?.ToJsonString()}");
                    }

                    if (entry.Status.Completed)
                    {
                        return entry;
                    }
                }

                await Task.Delay(interval, cancellationToken);
            }
        }

        /// <summary>
        /// Downloads an output (or input/temp) file from ComfyUI.
        /// </summary>
        public async Task<byte[]> GetFileAsync(ComfyFileRef file, CancellationToken cancellationToken = default)
        {
            var query = $"api/view?filename={Uri.EscapeDataString(file.Filename)}" +
                        $"&subfolder={Uri.EscapeDataString(file.Subfolder ?? string.Empty)}" +
                        $"&type={Uri.EscapeDataString(file.Type ?? "output")}";

            using var response = await httpClient.GetAsync(query, cancellationToken);

            // Comfy Cloud redirects to a signed storage URL; follow it without our auth headers.
            if (IsRedirect(response.StatusCode) && response.Headers.Location is Uri location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(httpClient.BaseAddress!, location);
                return await DownloadClient.GetByteArrayAsync(target, cancellationToken);
            }

            await EnsureSuccessAsync(response, $"downloading {file.Filename}", cancellationToken);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        /// <summary>
        /// Whether ComfyUI still has a file, without downloading it. Comfy Cloud answers with a redirect to storage
        /// for a file it has; a local server sends the file itself, so only the headers are read.
        /// </summary>
        public async Task<bool> FileExistsAsync(ComfyFileRef file, CancellationToken cancellationToken = default)
        {
            var query = $"api/view?filename={Uri.EscapeDataString(file.Filename)}" +
                        $"&subfolder={Uri.EscapeDataString(file.Subfolder ?? string.Empty)}" +
                        $"&type={Uri.EscapeDataString(file.Type ?? "output")}";

            using var response = await httpClient.GetAsync(query, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode || IsRedirect(response.StatusCode))
            {
                return true;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            await EnsureSuccessAsync(response, $"checking for {file.Filename}", cancellationToken);
            return false;
        }

        /// <summary>
        /// Queues a workflow, waits for it to finish and downloads every image it produced.
        /// </summary>
        public async Task<IReadOnlyList<ComfyOutputFile>> RunWorkflowAsync(JsonNode workflow, CancellationToken cancellationToken = default)
        {
            return (await RunWorkflowWithTextAsync(workflow, cancellationToken)).Files;
        }

        /// <summary>
        /// Like <see cref="RunWorkflowAsync"/>, but also returns the text nodes showed (e.g. a PreviewAny "Final
        /// Prompt"), by node id.
        /// </summary>
        public async Task<ComfyWorkflowResult> RunWorkflowWithTextAsync(JsonNode workflow, CancellationToken cancellationToken = default)
        {
            var queued = await QueuePromptAsync(workflow, cancellationToken);
            var entry = await WaitForCompletionAsync(queued.PromptId, cancellationToken: cancellationToken);

            var results = new List<ComfyOutputFile>();
            var text = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (nodeId, output) in entry.Outputs ?? [])
            {
                if (output.Text is { Count: > 0 } lines)
                {
                    text[nodeId] = lines;
                }

                foreach (var image in output.Images ?? [])
                {
                    if (image.Type == "temp")
                    {
                        // Preview nodes write temp files; only keep real outputs.
                        continue;
                    }

                    var data = await GetFileAsync(image, cancellationToken);
                    results.Add(new ComfyOutputFile(nodeId, image, data));
                }
            }

            return new ComfyWorkflowResult(results, text);
        }

        /// <summary>
        /// Uploads an image into ComfyUI's input folder so it can be referenced by a LoadImage node.
        /// </summary>
        public async Task<ComfyFileRef> UploadImageAsync(Stream image, string fileName, string? subfolder = null, bool overwrite = true, CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            var imageContent = new StreamContent(image);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(imageContent, "image", fileName);
            content.Add(new StringContent("input"), "type");
            content.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");
            if (!string.IsNullOrEmpty(subfolder))
            {
                content.Add(new StringContent(subfolder), "subfolder");
            }

            using var response = await httpClient.PostAsync("api/upload/image", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ComfyUploadResult>(cancellationToken)
                ?? throw new ComfyApiException("ComfyUI returned an empty response when uploading the image.");
            return new ComfyFileRef(result.Name, result.Subfolder, result.Type);
        }

        public async Task<JsonNode?> GetQueueAsync(CancellationToken cancellationToken = default)
        {
            return await httpClient.GetFromJsonAsync<JsonNode>("api/queue", cancellationToken);
        }

        public async Task<JsonNode?> GetSystemStatsAsync(CancellationToken cancellationToken = default)
        {
            return await httpClient.GetFromJsonAsync<JsonNode>("api/system_stats", cancellationToken);
        }

        public async Task<JsonNode?> GetObjectInfoAsync(string? nodeClass = null, CancellationToken cancellationToken = default)
        {
            if (nodeClass != null && isCloud)
            {
                // Comfy Cloud only serves the full list; pick the one node out of it.
                var all = await httpClient.GetFromJsonAsync<JsonObject>("api/object_info", cancellationToken);
                return all?[nodeClass] is JsonNode info ? new JsonObject { [nodeClass] = info.DeepClone() } : null;
            }

            var path = nodeClass == null ? "api/object_info" : $"api/object_info/{Uri.EscapeDataString(nodeClass)}";
            return await httpClient.GetFromJsonAsync<JsonNode>(path, cancellationToken);
        }

        /// <summary>
        /// Interrupts the currently executing prompt.
        /// </summary>
        public async Task InterruptAsync(CancellationToken cancellationToken = default)
        {
            using var response = await httpClient.PostAsync("api/interrupt", null, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Removes prompts from the pending queue.
        /// </summary>
        public async Task DeleteFromQueueAsync(IEnumerable<string> promptIds, CancellationToken cancellationToken = default)
        {
            using var response = await httpClient.PostAsJsonAsync("api/queue", new { delete = promptIds }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return (int)statusCode is >= 300 and < 400;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ComfyApiException($"ComfyUI request failed while {action} ({(int)response.StatusCode}): {error}");
            }
        }

        public void Dispose()
        {
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    public class ComfyApiException : Exception
    {
        public ComfyApiException(string message)
            : base(message)
        {
        }
    }

    public record ComfyQueueResult(
        [property: JsonPropertyName("prompt_id")] string PromptId,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("node_errors")] JsonNode? NodeErrors);

    public record ComfyHistoryEntry(
        [property: JsonPropertyName("prompt")] JsonNode? Prompt,
        [property: JsonPropertyName("outputs")] Dictionary<string, ComfyNodeOutput>? Outputs,
        [property: JsonPropertyName("status")] ComfyPromptStatus? Status);

    /// <param name="Text">Text shown by nodes such as PreviewAny, e.g. ["the final prompt"].</param>
    public record ComfyNodeOutput(
        [property: JsonPropertyName("images")] List<ComfyFileRef>? Images,
        [property: JsonPropertyName("text")] List<string>? Text = null);

    public record ComfyPromptStatus(
        [property: JsonPropertyName("status_str")] string? StatusStr,
        [property: JsonPropertyName("completed")] bool Completed,
        [property: JsonPropertyName("messages")] JsonNode? Messages);

    public record ComfyFileRef(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("subfolder")] string? Subfolder,
        [property: JsonPropertyName("type")] string? Type);

    // /upload/image returns "name" rather than "filename".
    internal record ComfyUploadResult(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("subfolder")] string? Subfolder,
        [property: JsonPropertyName("type")] string? Type);

    public record ComfyOutputFile(string NodeId, ComfyFileRef File, byte[] Data);

    /// <param name="Text">Text each node showed, by node id.</param>
    public record ComfyWorkflowResult(IReadOnlyList<ComfyOutputFile> Files, IReadOnlyDictionary<string, IReadOnlyList<string>> Text);
}
