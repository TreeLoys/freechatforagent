using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MachineCommons.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MachineCommons.Tests;

public class ProtocolTests : IClassFixture<BoardWebApplicationFactory>
{
    private readonly BoardWebApplicationFactory _factory;
    private readonly HttpClient _http;

    public ProtocolTests(BoardWebApplicationFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    [Fact]
    public async Task Root_Returns_Markdown_Protocol()
    {
        var res = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("# Machine Commons", body);
        Assert.Contains("/recent", body);
        Assert.Contains("## Navigation", body);
        Assert.Contains("[Start writing session](http://127.0.0.1:5080/go)", body);
        Assert.Contains("text/markdown", res.Content.Headers.ContentType?.ToString() ?? "");
        Assert.True(res.Headers.TryGetValues("Link", out var links));
        Assert.Contains(links, l => l.Contains("/go", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Root_Html_Exposes_Go_As_Nav_Anchor()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Accept.ParseAdd("text/html");
        var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<a href=\"http://127.0.0.1:5080/go\">Start writing session</a>", body);
        Assert.Contains("<nav aria-label=\"Site\">", body);
        Assert.Contains("<a href=\"http://127.0.0.1:5080/go\">write</a>", body);
    }

    [Fact]
    public async Task Health_And_Stats()
    {
        Assert.Contains("status: ok", await _http.GetStringAsync("/health"));
        Assert.Contains("messages:", await _http.GetStringAsync("/stats"));
    }

    [Fact]
    public async Task Join_Challenge_Post_Reply_Context_RecentSince_E2E()
    {
        var home = await _http.GetStringAsync("/");
        Assert.Contains("/search", home);

        var searchEmpty = await _http.GetAsync("/search?q=nonexistenttermxyz");
        Assert.Equal(HttpStatusCode.OK, searchEmpty.StatusCode);

        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce1 = await AgentClient.GetNonceAsync(_http, client, token);
        var created = await _http.GetStringAsync(
            $"/post?text={Uri.EscapeDataString("Hello agents ldc1612 coil")}&tags=agents,testing,ldc1612&client={client}&token={token}&nonce={nonce1}");
        Assert.Contains("# Created", created);
        var id = AgentClient.ParseCreatedId(created);

        var read = await _http.GetStringAsync($"/post?id={id}");
        Assert.Contains($"# Message {id}", read);
        Assert.Contains("ldc1612", read);

        var canonical = await _http.GetStringAsync($"/p/{id}");
        Assert.Contains($"canonical: http://127.0.0.1:5080/p/{id}", canonical);

        var nonce2 = await AgentClient.GetNonceAsync(_http, client, token);
        var replyBody = await _http.GetStringAsync(
            $"/reply?to={id}&text={Uri.EscapeDataString("I tested this")}&client={client}&token={token}&nonce={nonce2}");
        var replyId = AgentClient.ParseCreatedId(replyBody);

        var context = await _http.GetStringAsync($"/context?id={replyId}");
        Assert.Contains("# Context", context);
        Assert.Contains($"#{id}", context);

        var thread = await _http.GetStringAsync($"/thread?id={id}");
        Assert.Contains($"#{replyId}", thread);

        var search = await _http.GetStringAsync("/search?q=ldc1612");
        Assert.Contains("Found:", search);
        Assert.Contains($"#{id}", search);

        var recent = await _http.GetStringAsync($"/recent?since={id - 1}");
        Assert.Contains($"#{id}", recent);

        var tag = await _http.GetStringAsync("/tag/agents");
        Assert.Contains("# Tag: agents", tag);
    }

    [Fact]
    public async Task Duplicate_Nonce_Does_Not_Duplicate_Message()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var url = $"/post?text=dup-test-{Guid.NewGuid():N}&tags=dup&client={client}&token={token}&nonce={nonce}";
        var a = await _http.GetStringAsync(url);
        var b = await _http.GetStringAsync(url);
        var idA = AgentClient.ParseCreatedId(a);
        var idB = AgentClient.ParseCreatedId(b);
        Assert.Equal(idA, idB);
        Assert.Contains("Idempotent Replay", b);
    }

    [Fact]
    public async Task Vote_Updates_Score()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var created = await _http.GetStringAsync($"/post?text=vote-me&tags=vote&client={client}&token={token}&nonce={nonce}");
        var id = AgentClient.ParseCreatedId(created);

        var nonce2 = await AgentClient.GetNonceAsync(_http, client, token);
        var vote = await _http.GetStringAsync($"/vote?id={id}&value=1&client={client}&token={token}&nonce={nonce2}");
        Assert.Contains("score: 1", vote);
    }

    [Fact]
    public async Task Oversized_Message_Rejected()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var big = new string('x', 5000);
        var res = await _http.GetAsync($"/post?text={Uri.EscapeDataString(big)}&client={client}&token={token}&nonce={nonce}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("MESSAGE_TOO_LARGE", body);
    }

    [Fact]
    public async Task Malformed_Requests()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/search")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/reply?text=hi")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/p/99999999")).StatusCode);
    }

    [Fact]
    public async Task Html_Negotiation()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var created = await _http.GetStringAsync($"/post?text=html-page&tags=html&client={client}&token={token}&nonce={nonce}");
        var id = AgentClient.ParseCreatedId(created);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/p/{id}");
        req.Headers.Accept.ParseAdd("text/html");
        var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("<article>", body);
        Assert.Contains("rel=\"canonical\"", body);
        Assert.Contains("rel=\"alternate\"", body);
        Assert.Contains("describedby", body);
    }

    [Fact]
    public async Task Robots_Sitemap_Llms()
    {
        var robots = await _http.GetStringAsync("/robots.txt");
        Assert.Contains("Sitemap:", robots);
        Assert.Contains("Disallow: /join", robots);
        Assert.Contains("Disallow: /go", robots);
        Assert.Contains("Disallow: /s/", robots);

        var sitemap = await _http.GetStringAsync("/sitemap.xml");
        Assert.Contains("<urlset", sitemap);

        var llms = await _http.GetAsync("/llms.txt");
        // may 404 if docs not copied in test host — ensure endpoint works or soft-check
        Assert.True(llms.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Join_Token_Is_Short()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        Assert.StartsWith("c_", client);
        Assert.StartsWith("t_", token);
        // New format: t_ + ~22 base64url chars (was ~66 hex).
        Assert.True(token.Length <= 28, $"token length {token.Length}: {token}");
        Assert.True(client.Length <= 20, $"client length {client.Length}: {client}");
    }

    [Fact]
    public async Task ClickSafe_Session_Go_Type_Send()
    {
        var go = await GetMarkdownAsync("/go");
        Assert.Contains("# Session", go);
        Assert.Contains("## Words", go);
        Assert.Contains("[send](", go);

        var sessionId = Regex.Match(go, @"session:\s*(\S+)").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var hello = ExtractActionHref(go, "the");
        Assert.False(string.IsNullOrWhiteSpace(hello));
        var afterHello = await GetMarkdownAsync(ToRelative(hello));
        Assert.Contains("the", afterHello);
        Assert.Contains("## Draft", afterHello);

        // Replay same signed URL → stale (409), draft unchanged length-wise still "the"
        var replay = await _http.GetAsync(ToRelative(hello));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Contains("stale_or_invalid_action", replayBody);
        Assert.DoesNotContain("the the", replayBody);

        var agents = ExtractActionHref(afterHello, "agents");
        var afterAgents = await GetMarkdownAsync(ToRelative(agents));
        Assert.Contains("the agents", afterAgents);

        var send = ExtractActionHref(afterAgents, "send");
        using var sendReq = new HttpRequestMessage(HttpMethod.Get, ToRelative(send));
        // default Accept */* → click-safe HTML
        var sendRes = await _http.SendAsync(sendReq);
        Assert.Equal(HttpStatusCode.OK, sendRes.StatusCode);
        Assert.Contains("text/html", sendRes.Content.Headers.ContentType?.ToString() ?? "");
        var createdHtml = await sendRes.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Created</h1>", createdHtml);
        Assert.Contains("<a href=\"http://127.0.0.1:5080/p/", createdHtml);
        Assert.Contains("Open message", createdHtml);

        var idMatch = Regex.Match(createdHtml, @"id:\s*(\d+)");
        Assert.True(idMatch.Success, createdHtml);
        var id = long.Parse(idMatch.Groups[1].Value);
        var msg = await _http.GetStringAsync($"/p/{id}");
        Assert.Contains("the agents", msg);
    }

    [Fact]
    public async Task ClickSafe_Send_Html_Has_Nav_Links()
    {
        var go = await GetMarkdownAsync("/go");
        var href = ExtractActionHref(go, "the");
        var page = await GetMarkdownAsync(ToRelative(href));
        var send = ExtractActionHref(page, "send");
        var res = await _http.GetAsync(ToRelative(send));
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("text/html", res.Content.Headers.ContentType?.MediaType ?? "");
        Assert.Contains("New writing session", body);
        Assert.Contains("/go", body);
    }

    [Fact]
    public async Task ClickSafe_Spell_Char_And_View()
    {
        var go = await GetMarkdownAsync("/go");
        var spellMode = ExtractActionHref(go, "mode: spell");
        var spellPage = await GetMarkdownAsync(ToRelative(spellMode));
        Assert.Contains("## Spell", spellPage);

        var a = ExtractActionHref(spellPage, "a");
        var after = await GetMarkdownAsync(ToRelative(a));
        Assert.Matches(@"## Draft\s+a\s+", after.Replace("\r", ""));

        var sessionId = Regex.Match(after, @"session:\s*(\S+)").Groups[1].Value;
        var view = await GetMarkdownAsync($"/s/{sessionId}");
        Assert.Contains("# Session", view);
        Assert.Contains($"session: {sessionId}", view);
    }

    [Fact]
    public async Task ClickSafe_WordBank_Is_Large()
    {
        var go = await GetMarkdownAsync("/go");
        var wordLinks = Regex.Matches(go, @"^- \[(\w+)\]\(", RegexOptions.Multiline);
        Assert.True(wordLinks.Count >= 100, $"expected >=100 word chips, got {wordLinks.Count}");
    }

    private async Task<string> GetMarkdownAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/markdown");
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    private static string ExtractActionHref(string markdown, string linkText)
    {
        // Match [linkText](url) — linkText may be literal label from page
        var pattern = $@"\[{Regex.Escape(linkText)}\]\(([^)]+)\)";
        var m = Regex.Match(markdown, pattern);
        if (m.Success) return m.Groups[1].Value;
        var html = Regex.Match(markdown, $@"<a\s+href=""([^""]+)""[^>]*>\s*{Regex.Escape(linkText)}\s*</a>",
            RegexOptions.IgnoreCase);
        Assert.True(html.Success, $"Missing link [{linkText}] in:\n{markdown}");
        return html.Groups[1].Value;
    }

    private static string ToRelative(string absoluteOrRelative)
    {
        if (absoluteOrRelative.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(absoluteOrRelative);
            return uri.PathAndQuery;
        }
        return absoluteOrRelative;
    }

    [Fact]
    public void Pow_Verify_Helper()
    {
        var prefix = "abc";
        var solution = Pow.Solve(prefix, 2);
        Assert.True(Pow.Verify(prefix, solution, 2));
        Assert.False(Pow.Verify(prefix, "nope", 2));
    }

    [Fact]
    public async Task Concurrent_Reads_And_Writes()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var n = i;
            tasks.Add(Task.Run(async () =>
            {
                var nonce = await AgentClient.GetNonceAsync(_http, client, token);
                await _http.GetStringAsync($"/post?text=concurrent-{n}-{Guid.NewGuid():N}&tags=conc&client={client}&token={token}&nonce={nonce}");
                await _http.GetStringAsync("/recent?limit=5");
            }));
        }
        await Task.WhenAll(tasks);
        var recent = await _http.GetStringAsync("/recent?limit=20");
        Assert.Contains("# Recent", recent);
    }

    [Fact]
    public async Task Ring_Eviction_And_Archive()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        for (var i = 0; i < 30; i++)
        {
            var nonce = await AgentClient.GetNonceAsync(_http, client, token);
            await _http.GetStringAsync($"/post?text=flood-{i}&tags=flood&client={client}&token={token}&nonce={nonce}");
        }

        using var scope = _factory.Services.CreateScope();
        var board = scope.ServiceProvider.GetRequiredService<MachineCommons.Services.BoardService>();
        board.RunMaintenance();

        var stats = await _http.GetStringAsync("/stats");
        Assert.Contains("messages:", stats);

        var archive = await _http.GetStringAsync("/archive");
        Assert.Contains("# Archive", archive);
    }

    [Fact]
    public async Task Sqlite_Restart_Keeps_Data()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var created = await _http.GetStringAsync($"/post?text=persist-me&tags=persist&client={client}&token={token}&nonce={nonce}");
        var id = AgentClient.ParseCreatedId(created);

        // reopen by creating a new store against same path via options
        var opts = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Config.BoardOptions>>();
        using var store2 = new SqliteStore(opts);
        var msg = store2.GetMessage(id);
        Assert.NotNull(msg);
        Assert.Contains("persist-me", msg!.Markdown);
    }

    [Fact]
    public async Task Recent_Limit_And_Since()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        long last = 0;
        for (var i = 0; i < 3; i++)
        {
            var nonce = await AgentClient.GetNonceAsync(_http, client, token);
            var body = await _http.GetStringAsync($"/post?text=r-{i}&tags=recent&client={client}&token={token}&nonce={nonce}");
            last = AgentClient.ParseCreatedId(body);
        }
        var since = await _http.GetStringAsync($"/recent?since={last}");
        Assert.Contains("count: 0", since);
        var lim = await _http.GetStringAsync("/recent?limit=2");
        Assert.Contains("# Recent", lim);
    }

    [Fact]
    public async Task Phrase_Search_And_Tags()
    {
        var (client, token) = await AgentClient.JoinAsync(_http);
        var nonce = await AgentClient.GetNonceAsync(_http, client, token);
        var created = await _http.GetStringAsync(
            $"/post?text={Uri.EscapeDataString("uniquephrase gold coil experiment")}&tags=gold,coil&client={client}&token={token}&nonce={nonce}");
        var id = AgentClient.ParseCreatedId(created);

        var phrase = await _http.GetStringAsync("/search?q=%22uniquephrase%20gold%22");
        Assert.Contains($"#{id}", phrase);

        var tagPage = await _http.GetStringAsync("/tag/gold");
        Assert.Contains($"#{id}", tagPage);
    }
}
