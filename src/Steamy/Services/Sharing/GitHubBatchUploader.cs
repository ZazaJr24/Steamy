using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Steamy.Services;

/// <summary>A file to commit: repository path and raw content.</summary>
public sealed record UploadFile(string Path, byte[] Content);

/// <summary>Progress of a batch upload; <see cref="Done"/> counts files whose content is on GitHub.</summary>
public sealed record UploadProgress(int Done, int Total, string Message);

public sealed record BatchUploadResult(
    bool Succeeded,
    string Message,
    string? CommitSha = null,
    string? CommitUrl = null,
    int FilesSent = 0,
    bool UsedContentsApi = false);

/// <summary>
/// Commits many files to one branch as a single commit through the Git Data API
/// (blobs → tree → commit → move the branch). One commit per batch instead of one per file keeps
/// the repository history readable and needs far fewer requests.
/// <para>
/// An empty repository has no branch to build on, so the first batch there falls back to the
/// contents API (one commit per file), which creates the branch. Secondary rate limits are
/// honoured by waiting as GitHub asks, and a branch that moved while the batch was being built is
/// retried once on top of the new head.
/// </para>
/// </summary>
public sealed class GitHubBatchUploader
{
    private const int PacingThreshold = 60;
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GitHubBatchUploader(HttpClient http, string userAgent, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _userAgent = userAgent;
        _delay = delay ?? Task.Delay;
    }

    public string ApiBase { get; init; } = "https://api.github.com";

    public async Task<BatchUploadResult> UploadAsync(GitHubTarget target, string token, string message,
        IReadOnlyList<UploadFile> files, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return new BatchUploadResult(false, "There is nothing to send.");
        if (string.IsNullOrWhiteSpace(token)) return new BatchUploadResult(false, "No sharing token is stored.");

        var repoUrl = $"{ApiBase.TrimEnd('/')}/repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}";
        var branchPath = string.Join('/', target.Branch.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        progress?.Report(new UploadProgress(0, files.Count, $"Connecting to {target}…"));
        var head = await SendAsync(HttpMethod.Get, $"{repoUrl}/git/ref/heads/{branchPath}", null, token, cancellationToken).ConfigureAwait(false);

        // 409: the repository has no commits yet. 404: the branch does not exist (or the token
        // cannot see the repository — the contents API then reports that with a clear hint).
        if (head.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            return await UploadWithContentsApiAsync(repoUrl, target, token, message, files, emptyRepository: head.Status == HttpStatusCode.Conflict,
                progress, cancellationToken).ConfigureAwait(false);

        if (!head.IsSuccess) return Fail(head, target);

        // Blobs first: they do not depend on the branch head, so a retry below can reuse them.
        var blobs = new List<(string Path, string Sha)>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var blob = await SendAsync(HttpMethod.Post, $"{repoUrl}/git/blobs",
                new JsonObject { ["content"] = Convert.ToBase64String(file.Content), ["encoding"] = "base64" },
                token, cancellationToken).ConfigureAwait(false);
            if (!blob.IsSuccess) return Fail(blob, target);

            blobs.Add((file.Path, blob.Read("sha")!));
            progress?.Report(new UploadProgress(index + 1, files.Count, $"Uploaded {index + 1} of {files.Count}"));
            if (files.Count > PacingThreshold && index < files.Count - 1)
                await _delay(PacingDelay, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            var parentSha = head.ReadPath("object", "sha");
            if (parentSha is null) return new BatchUploadResult(false, "GitHub returned an unexpected answer for the branch.");

            var parent = await SendAsync(HttpMethod.Get, $"{repoUrl}/git/commits/{parentSha}", null, token, cancellationToken).ConfigureAwait(false);
            if (!parent.IsSuccess) return Fail(parent, target);

            var entries = new JsonArray();
            foreach (var (path, sha) in blobs)
                entries.Add(new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = sha });

            progress?.Report(new UploadProgress(files.Count, files.Count, "Writing the commit…"));
            var tree = await SendAsync(HttpMethod.Post, $"{repoUrl}/git/trees",
                new JsonObject { ["base_tree"] = parent.ReadPath("tree", "sha"), ["tree"] = entries },
                token, cancellationToken).ConfigureAwait(false);
            if (!tree.IsSuccess) return Fail(tree, target);

            var commit = await SendAsync(HttpMethod.Post, $"{repoUrl}/git/commits",
                new JsonObject { ["message"] = message, ["tree"] = tree.Read("sha"), ["parents"] = new JsonArray(parentSha) },
                token, cancellationToken).ConfigureAwait(false);
            if (!commit.IsSuccess) return Fail(commit, target);

            var commitSha = commit.Read("sha")!;
            var update = await SendAsync(HttpMethod.Patch, $"{repoUrl}/git/refs/heads/{branchPath}",
                new JsonObject { ["sha"] = commitSha, ["force"] = false },
                token, cancellationToken).ConfigureAwait(false);

            if (update.IsSuccess)
            {
                progress?.Report(new UploadProgress(files.Count, files.Count, "Done."));
                return new BatchUploadResult(true, $"Committed {files.Count} file(s) to {target} in one commit.",
                    commitSha, commit.Read("html_url"), files.Count);
            }

            // Somebody else pushed while this batch was being built: rebuild on the new head once.
            if (update.Status == HttpStatusCode.UnprocessableEntity && attempt == 0)
            {
                head = await SendAsync(HttpMethod.Get, $"{repoUrl}/git/ref/heads/{branchPath}", null, token, cancellationToken).ConfigureAwait(false);
                if (!head.IsSuccess) return Fail(head, target);
                continue;
            }

            return Fail(update, target);
        }
    }

    private async Task<BatchUploadResult> UploadWithContentsApiAsync(string repoUrl, GitHubTarget target, string token, string message,
        IReadOnlyList<UploadFile> files, bool emptyRepository, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
    {
        string? lastUrl = null;
        string? lastSha = null;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var body = new JsonObject
            {
                ["message"] = files.Count == 1 ? message : $"{message} ({index + 1}/{files.Count})",
                ["content"] = Convert.ToBase64String(file.Content)
            };
            // An empty repository has no branch yet: the first file creates the default branch, and
            // the rest follow it there, whatever branch name is configured.
            if (!emptyRepository) body["branch"] = target.Branch;

            var path = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
            var response = await SendAsync(HttpMethod.Put, $"{repoUrl}/contents/{path}", body, token, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess) return Fail(response, target) with { FilesSent = index, UsedContentsApi = true };

            lastUrl = response.ReadPath("commit", "html_url") ?? lastUrl;
            lastSha = response.ReadPath("commit", "sha") ?? lastSha;
            progress?.Report(new UploadProgress(index + 1, files.Count, $"Sent {index + 1} of {files.Count}"));
            if (files.Count > PacingThreshold && index < files.Count - 1)
                await _delay(PacingDelay, cancellationToken).ConfigureAwait(false);
        }

        return new BatchUploadResult(true, $"Sent {files.Count} file(s) to {target}.", lastSha, lastUrl, files.Count, UsedContentsApi: true);
    }

    private async Task<ApiResponse> SendAsync(HttpMethod method, string url, JsonNode? body, string token, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = new ApiResponse(response.StatusCode, text);

            var wait = RateLimitWait(response, text);
            if (wait is null || attempt >= 3) return result;
            await _delay(wait.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long GitHub asks to wait before the next try, or null when this is not a rate limit.</summary>
    private static TimeSpan? RateLimitWait(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;

        if (response.Headers.RetryAfter?.Delta is { } delta) return Clamp(delta);
        if (response.Headers.TryGetValues("retry-after", out var retry) && int.TryParse(retry.FirstOrDefault(), out var seconds))
            return Clamp(TimeSpan.FromSeconds(seconds));

        var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        if (exhausted && response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var resetUnix))
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(resetUnix) - DateTimeOffset.UtcNow);

        return body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(30) : null;
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > MaxRateLimitWait ? MaxRateLimitWait : wait;

    private static BatchUploadResult Fail(ApiResponse response, GitHubTarget target)
    {
        var hint = (int)response.Status switch
        {
            401 => "The sharing token is not valid any more. Create a new fine-grained token in Settings.",
            403 => $"The token has no write access to {target}, or GitHub rate-limited the upload. Try again in a minute.",
            404 => $"{target} (branch {target.Branch}) was not found, or the token cannot see it. Fine-grained tokens must be granted this repository.",
            409 => "The repository is in a state that does not accept commits right now.",
            422 => "GitHub rejected the commit. Check the branch name in Settings.",
            _ => $"GitHub answered HTTP {(int)response.Status}."
        };

        var detail = response.Read("message");
        return new BatchUploadResult(false, string.IsNullOrWhiteSpace(detail) ? hint : $"{hint} GitHub said: {detail}");
    }

    private sealed record ApiResponse(HttpStatusCode Status, string Body)
    {
        private JsonNode? _json;
        private bool _parsed;

        public bool IsSuccess => (int)Status is >= 200 and < 300;

        public string? Read(string property) => ReadPath(property);

        public string? ReadPath(params string[] path)
        {
            if (!_parsed)
            {
                _parsed = true;
                try { _json = JsonNode.Parse(Body); }
                catch (JsonException) { _json = null; }
            }

            var node = _json;
            foreach (var part in path)
            {
                if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node)) return null;
            }

            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
    }
}
