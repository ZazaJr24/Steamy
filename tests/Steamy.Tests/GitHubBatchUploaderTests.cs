using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Steamy.Services;

namespace Steamy.Tests;

public class GitHubBatchUploaderTests
{
    private static readonly GitHubTarget Target = new("owner", "dumps", "main");
    private const string Repo = "https://api.github.com/repos/owner/dumps";

    /// <summary>Answers requests from a queue of scripted responses and records what was sent.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, string?, HttpResponseMessage>> _responses = new();

        public List<(HttpMethod Method, string Url, JsonNode? Body, string? Auth)> Requests { get; } = new();

        public ScriptedHandler Then(HttpStatusCode status, string json, Action<HttpResponseMessage>? tweak = null)
        {
            _responses.Enqueue((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                tweak?.Invoke(response);
                return response;
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body is null ? null : JsonNode.Parse(body),
                request.Headers.Authorization?.ToString() ?? request.Headers.GetValues("Authorization").FirstOrDefault()));
            if (_responses.Count == 0) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            return _responses.Dequeue()(request, body);
        }
    }

    private static (GitHubBatchUploader Uploader, List<TimeSpan> Waits) Create(ScriptedHandler handler)
    {
        var waits = new List<TimeSpan>();
        var uploader = new GitHubBatchUploader(new HttpClient(handler), "Steamy/test", (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (uploader, waits);
    }

    private static readonly UploadFile[] TwoFiles =
    {
        new("dumps/730/730-a.zip", new byte[] { 1, 2, 3 }),
        new("batches/b.json", Encoding.UTF8.GetBytes("{}"))
    };

    [Fact]
    public async Task Many_files_become_one_commit_on_top_of_the_branch()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"object":{"sha":"parent"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"blob1"}""")
            .Then(HttpStatusCode.Created, """{"sha":"blob2"}""")
            .Then(HttpStatusCode.OK, """{"sha":"parent","tree":{"sha":"basetree"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"newtree"}""")
            .Then(HttpStatusCode.Created, """{"sha":"commit1","html_url":"https://github.com/owner/dumps/commit/commit1"}""")
            .Then(HttpStatusCode.OK, """{"object":{"sha":"commit1"}}""");
        var (uploader, _) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share 2 apps", TwoFiles);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("commit1", result.CommitSha);
        Assert.Equal("https://github.com/owner/dumps/commit/commit1", result.CommitUrl);
        Assert.False(result.UsedContentsApi);

        var urls = handler.Requests.Select(request => $"{request.Method} {request.Url}").ToList();
        Assert.Equal(new[]
        {
            $"GET {Repo}/git/ref/heads/main",
            $"POST {Repo}/git/blobs",
            $"POST {Repo}/git/blobs",
            $"GET {Repo}/git/commits/parent",
            $"POST {Repo}/git/trees",
            $"POST {Repo}/git/commits",
            $"PATCH {Repo}/git/refs/heads/main"
        }, urls);

        Assert.Equal("AQID", handler.Requests[1].Body!["content"]!.GetValue<string>());
        var tree = handler.Requests[4].Body!;
        Assert.Equal("basetree", tree["base_tree"]!.GetValue<string>());
        Assert.Equal(new[] { "dumps/730/730-a.zip", "batches/b.json" }, tree["tree"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>()));
        Assert.Equal("parent", handler.Requests[5].Body!["parents"]![0]!.GetValue<string>());
        Assert.False(handler.Requests[6].Body!["force"]!.GetValue<bool>());
        Assert.All(handler.Requests, request => Assert.Equal("Bearer tok", request.Auth));
    }

    [Fact]
    public async Task Empty_repository_falls_back_to_the_contents_api_on_the_default_branch()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.Conflict, """{"message":"Git Repository is empty."}""")
            .Then(HttpStatusCode.Created, """{"commit":{"sha":"c1","html_url":"u1"}}""")
            .Then(HttpStatusCode.Created, """{"commit":{"sha":"c2","html_url":"u2"}}""");
        var (uploader, _) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share", TwoFiles);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.UsedContentsApi);
        Assert.Equal("c2", result.CommitSha);
        Assert.Equal($"{Repo}/contents/dumps/730/730-a.zip", handler.Requests[1].Url);
        Assert.Null(handler.Requests[1].Body!["branch"]);
        Assert.Null(handler.Requests[2].Body!["branch"]);
    }

    [Fact]
    public async Task Rate_limits_are_waited_out_and_retried()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"object":{"sha":"p"}}""")
            .Then(HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit"}""",
                response => response.Headers.TryAddWithoutValidation("Retry-After", "7"))
            .Then(HttpStatusCode.Created, """{"sha":"b1"}""")
            .Then(HttpStatusCode.OK, """{"tree":{"sha":"t"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"t2"}""")
            .Then(HttpStatusCode.Created, """{"sha":"c"}""")
            .Then(HttpStatusCode.OK, "{}");
        var (uploader, waits) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share", TwoFiles.Take(1).ToList());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
    }

    [Fact]
    public async Task A_branch_that_moved_meanwhile_is_rebuilt_once_on_the_new_head()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"object":{"sha":"old"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"b1"}""")
            .Then(HttpStatusCode.OK, """{"tree":{"sha":"t-old"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"tree1"}""")
            .Then(HttpStatusCode.Created, """{"sha":"c-old"}""")
            .Then(HttpStatusCode.UnprocessableEntity, """{"message":"Update is not a fast forward"}""")
            .Then(HttpStatusCode.OK, """{"object":{"sha":"new"}}""")
            .Then(HttpStatusCode.OK, """{"tree":{"sha":"t-new"}}""")
            .Then(HttpStatusCode.Created, """{"sha":"tree2"}""")
            .Then(HttpStatusCode.Created, """{"sha":"c-new"}""")
            .Then(HttpStatusCode.OK, "{}");
        var (uploader, _) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share", TwoFiles.Take(1).ToList());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("c-new", result.CommitSha);
        Assert.Equal(1, handler.Requests.Count(request => request.Url.EndsWith("/git/blobs", StringComparison.Ordinal)));
        Assert.Equal("new", handler.Requests[9].Body!["parents"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_branch_uses_the_contents_api_with_the_configured_branch()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
            .Then(HttpStatusCode.NotFound, """{"message":"Branch main not found"}""");
        var (uploader, _) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share", TwoFiles);

        Assert.False(result.Succeeded);
        Assert.True(result.UsedContentsApi);
        Assert.Equal(0, result.FilesSent);
        Assert.Equal("main", handler.Requests[1].Body!["branch"]!.GetValue<string>());
        Assert.Contains("Branch main not found", result.Message);
    }

    [Fact]
    public async Task A_rejected_token_is_reported_with_a_hint()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        var (uploader, waits) = Create(handler);

        var result = await uploader.UploadAsync(Target, "tok", "Share", TwoFiles);

        Assert.False(result.Succeeded);
        Assert.Contains("token", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bad credentials", result.Message);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Nothing_is_sent_without_files_or_token()
    {
        var handler = new ScriptedHandler();
        var (uploader, _) = Create(handler);

        Assert.False((await uploader.UploadAsync(Target, "tok", "m", Array.Empty<UploadFile>())).Succeeded);
        Assert.False((await uploader.UploadAsync(Target, " ", "m", TwoFiles)).Succeeded);
        Assert.Empty(handler.Requests);
    }
}
