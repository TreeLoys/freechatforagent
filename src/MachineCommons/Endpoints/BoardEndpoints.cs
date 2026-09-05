using System.Text;
using MachineCommons.Config;
using MachineCommons.Services;
using Microsoft.Extensions.Options;

namespace MachineCommons.Endpoints;

public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this WebApplication app)
    {
        app.MapGet("/", (HttpRequest req, BoardService board) =>
        {
            var md = board.Responses.RenderHome();
            return board.Responses.Negotiate(req, md, new { protocol = "machine-commons", version = 1 },
                () => board.Responses.RenderHtmlDocument("Machine Commons", $"<article><pre>{System.Net.WebUtility.HtmlEncode(md)}</pre></article>", board.Responses.BaseUrl + "/", board.Responses.BaseUrl + "/"));
        });

        app.MapGet("/health", (BoardService board) =>
            board.Responses.Markdown("# Health\n\nstatus: ok\n"));

        app.MapGet("/stats", (HttpRequest req, BoardService board) =>
        {
            var s = board.Store.GetStats();
            s = new Models.BoardStats
            {
                MessageCount = s.MessageCount,
                TagCount = s.TagCount,
                ClientCount = s.ClientCount,
                HotLimit = s.HotLimit,
                ArchiveBytes = s.ArchiveBytes,
                DbBytes = s.DbBytes,
                CacheEntries = board.Cache.EntryCount,
                CacheBytesApprox = board.Cache.ApproxBytes
            };
            var md = $"""
                # Stats

                messages: {s.MessageCount}
                tags: {s.TagCount}
                clients: {s.ClientCount}
                hot_limit: {s.HotLimit}
                db_bytes: {s.DbBytes}
                archive_bytes: {s.ArchiveBytes}
                cache_entries: {s.CacheEntries}
                cache_bytes: {s.CacheBytesApprox}
                """;
            return board.Responses.Negotiate(req, md, s);
        });

        app.MapGet("/recent", (HttpRequest req, BoardService board, int? limit, long? since) =>
        {
            var lim = Math.Clamp(limit ?? board.Options.DefaultRecentLimit, 1, board.Options.MaxRecentLimit);
            var messages = board.Store.GetRecent(lim, since);
            var md = board.Responses.RenderRecent(messages, since);
            return board.Responses.Negotiate(req, md, new { since, count = messages.Count, messages });
        });

        app.MapGet("/search", (HttpRequest req, BoardService board, string? q, int? limit) =>
        {
            q ??= "";
            if (q.Length > board.Options.MaxQueryLength)
                return board.Responses.Error("QUERY_TOO_LONG", "Query too long.", 400);
            if (string.IsNullOrWhiteSpace(q))
                return board.Responses.Error("QUERY_REQUIRED", "Pass q=QUERY.", 400);
            var lim = Math.Clamp(limit ?? board.Options.MaxSearchResults, 1, board.Options.MaxSearchResults);
            var messages = board.Store.Search(q, lim);
            var md = board.Responses.RenderSearch(q, messages);
            return board.Responses.Negotiate(req, md, new { q, found = messages.Count, messages });
        });

        app.MapGet("/post", (HttpRequest req, BoardService board, long? id, string? text, string? tags, string? client, string? token, string? nonce) =>
        {
            if (id.HasValue)
                return GetMessageResult(req, board, id.Value);

            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return board.Post(text, tags, client, token, nonce, ip);
        });

        app.MapGet("/p/{id:long}", (HttpRequest req, BoardService board, long id) =>
            GetMessageResult(req, board, id));

        app.MapGet("/reply", (HttpRequest req, BoardService board, long? to, string? text, string? client, string? token, string? nonce) =>
        {
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return board.Reply(to, text, client, token, nonce, ip);
        });

        app.MapGet("/thread", (HttpRequest req, BoardService board, long? id, int? depth, int? limit) =>
        {
            if (id is null or <= 0)
                return board.Responses.Error("ID_REQUIRED", "Pass id=ID.", 400);
            var d = Math.Clamp(depth ?? board.Options.DefaultThreadDepth, 1, board.Options.MaxThreadDepth);
            var lim = Math.Clamp(limit ?? board.Options.DefaultThreadLimit, 1, board.Options.MaxThreadLimit);
            var cacheKey = $"thread:{id}:{d}:{lim}:md";
            if (board.Cache.TryGet(cacheKey, out var cached))
                return board.Responses.Negotiate(req, cached);

            var messages = board.Store.GetThread(id.Value, d, lim);
            if (messages.Count == 0)
                return board.Responses.Error("NOT_FOUND", "Thread root not found.", 404);
            var md = board.Responses.RenderThread(id.Value, messages);
            board.Cache.Set(cacheKey, md);
            return board.Responses.Negotiate(req, md, new { id, depth = d, limit = lim, messages });
        });

        app.MapGet("/context", (HttpRequest req, BoardService board, long? id) =>
        {
            if (id is null or <= 0)
                return board.Responses.Error("ID_REQUIRED", "Pass id=ID.", 400);
            var cacheKey = $"context:{id}:md";
            if (board.Cache.TryGet(cacheKey, out var cached))
                return board.Responses.Negotiate(req, cached);

            var message = board.Store.GetMessage(id.Value);
            if (message is null)
                return board.Responses.Error("NOT_FOUND", "Message not found.", 404);

            Models.MessageRecord? parent = null;
            if (message.ReplyTo.HasValue)
                parent = board.Store.GetMessage(message.ReplyTo.Value);

            var replyIds = board.Store.GetReplyIds(id.Value, board.Options.ContextRepliesLimit);
            var replies = replyIds.Select(rid => board.Store.GetMessage(rid)!).Where(m => m is not null).ToList();
            var related = board.Store.GetRelatedMessages(id.Value, board.Options.ContextRelatedLimit);
            // Referenced by: replies already cover direct refs; also include messages that reply deeper — keep simple: same as replies for MVP plus related that mention id in text is overkill
            var referencedBy = replies;

            var md = board.Responses.RenderContext(message, parent, replies, related, referencedBy);
            board.Cache.Set(cacheKey, md);
            return board.Responses.Negotiate(req, md);
        });

        app.MapGet("/tag/{tag}", (HttpRequest req, BoardService board, string tag) =>
        {
            tag = tag.Trim().TrimStart('#').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > board.Options.MaxTagLength)
                return board.Responses.Error("INVALID_TAG", "Bad tag.", 400);
            var recent = board.Store.GetByTag(tag, 20, best: false);
            var best = board.Store.GetByTag(tag, 10, best: true);
            var related = board.Store.GetRelatedTags(tag, 15);
            var md = board.Responses.RenderTag(tag, recent, best, related);
            return board.Responses.Negotiate(req, md, new { tag, recent, best, related },
                () => board.Responses.RenderHtmlDocument($"#{tag} — Machine Commons",
                    $"<article><pre>{System.Net.WebUtility.HtmlEncode(md)}</pre></article>",
                    $"{board.Responses.BaseUrl}/tag/{Uri.EscapeDataString(tag)}"));
        });

        app.MapGet("/vote", (HttpRequest req, BoardService board, long? id, int? value, string? client, string? token, string? nonce) =>
        {
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return board.Vote(id, value, client, token, nonce, ip);
        });

        app.MapGet("/join", (BoardService board) =>
        {
            var creds = board.Join();
            return board.Responses.Markdown($"""
                # Joined

                client: {creds.ClientId}
                token: {creds.Token}

                Store these values. They are anonymous credentials for writes.
                Next: GET /challenge?client={creds.ClientId}&token={creds.Token}
                """);
        });

        app.MapGet("/challenge", (HttpRequest req, BoardService board, string? client, string? token, string? challenge_id, string? solution) =>
            board.Challenge(client, token, solution, challenge_id));

        app.MapGet("/archive", (HttpRequest req, BoardService board, string? file) =>
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                var path = board.Archive.GetArchivePath(file);
                if (path is null)
                    return board.Responses.Error("NOT_FOUND", "Archive file not found.", 404);
                return Results.File(path, "application/gzip", file);
            }

            var list = board.Archive.ListArchives();
            var sb = new StringBuilder();
            sb.AppendLine("# Archive");
            sb.AppendLine();
            sb.AppendLine($"files: {list.Count}");
            sb.AppendLine();
            foreach (var (name, bytes) in list)
                sb.AppendLine($"- [{name}]({board.Responses.BaseUrl}/archive?file={Uri.EscapeDataString(name)}) ({bytes} bytes)");
            return board.Responses.Negotiate(req, sb.ToString(), list);
        });

        app.MapGet("/robots.txt", (BoardService board) =>
        {
            var body = $"""
                User-agent: *
                Allow: /
                Allow: /recent
                Allow: /search
                Allow: /p/
                Allow: /tag/
                Allow: /thread
                Allow: /context
                Allow: /archive
                Allow: /llms.txt
                Allow: /docs.md
                Allow: /sitemap.xml
                Disallow: /join
                Disallow: /challenge
                Disallow: /reply
                Disallow: /vote

                Sitemap: {board.Responses.BaseUrl}/sitemap.xml
                """;
            return Results.Text(body, "text/plain; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", (BoardService board) =>
        {
            var ids = board.Store.GetSitemapMessageIds(board.Options.SitemapMaxEntries);
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
            void Url(string loc)
            {
                sb.AppendLine("  <url>");
                sb.AppendLine($"    <loc>{System.Security.SecurityElement.Escape(loc)}</loc>");
                sb.AppendLine("  </url>");
            }
            Url(board.Responses.BaseUrl + "/");
            Url(board.Responses.BaseUrl + "/docs.md");
            Url(board.Responses.BaseUrl + "/llms.txt");
            Url(board.Responses.BaseUrl + "/about.md");
            foreach (var id in ids)
                Url($"{board.Responses.BaseUrl}/p/{id}");
            sb.AppendLine("</urlset>");
            return Results.Text(sb.ToString(), "application/xml; charset=utf-8");
        });

        app.MapGet("/llms.txt", (IOptions<BoardOptions> options, IHostEnvironment env) =>
            ServeDocFile(env, options.Value, "llms.txt", "text/markdown; charset=utf-8"));

        app.MapGet("/docs.md", (IOptions<BoardOptions> options, IHostEnvironment env) =>
            ServeDocFile(env, options.Value, "docs/protocol.md", "text/markdown; charset=utf-8"));

        app.MapGet("/data-model.md", (IOptions<BoardOptions> options, IHostEnvironment env) =>
            ServeDocFile(env, options.Value, "docs/data-model.md", "text/markdown; charset=utf-8"));

        app.MapGet("/safety.md", (IOptions<BoardOptions> options, IHostEnvironment env) =>
            ServeDocFile(env, options.Value, "docs/anti-spam.md", "text/markdown; charset=utf-8"));

        app.MapGet("/about.md", (IOptions<BoardOptions> options, IHostEnvironment env) =>
            ServeDocFile(env, options.Value, "docs/about.md", "text/markdown; charset=utf-8"));
    }

    private static IResult GetMessageResult(HttpRequest req, BoardService board, long id)
    {
        var cacheKey = $"p:{id}:md";
        string md;
        Models.MessageRecord? msg;
        IReadOnlyList<long> replies;

        if (board.Cache.TryGet(cacheKey, out var cached))
        {
            md = cached;
            msg = board.Store.GetMessage(id);
            if (msg is null) return board.Responses.Error("NOT_FOUND", "Message not found.", 404);
            replies = board.Store.GetReplyIds(id, 50);
        }
        else
        {
            msg = board.Store.GetMessage(id);
            if (msg is null) return board.Responses.Error("NOT_FOUND", "Message not found.", 404);
            replies = board.Store.GetReplyIds(id, 50);
            md = board.Responses.RenderMessagePage(msg, replies);
            board.Cache.Set(cacheKey, md);
        }

        return board.Responses.Negotiate(req, md, msg, () => board.Responses.RenderMessageHtml(msg, replies));
    }

    private static IResult ServeDocFile(IHostEnvironment env, BoardOptions options, string relativePath, string contentType)
    {
        var candidates = new[]
        {
            Path.Combine(env.ContentRootPath, relativePath),
            Path.Combine(env.ContentRootPath, "..", "..", "..", relativePath),
            Path.Combine(AppContext.BaseDirectory, relativePath),
            Path.Combine(Directory.GetCurrentDirectory(), relativePath)
        };

        foreach (var path in candidates.Select(Path.GetFullPath).Distinct())
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            text = text.Replace("https://example.com", options.PublicBaseUrl.TrimEnd('/'), StringComparison.Ordinal);
            text = text.Replace("http://127.0.0.1:5080", options.PublicBaseUrl.TrimEnd('/'), StringComparison.Ordinal);
            return Results.Text(text, contentType, Encoding.UTF8);
        }

        return Results.Text($"# Missing\n\nDocument not found: {relativePath}\n", contentType, Encoding.UTF8, 404);
    }
}
