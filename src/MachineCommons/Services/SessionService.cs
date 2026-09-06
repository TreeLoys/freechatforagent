using System.Security.Cryptography;
using System.Text;
using MachineCommons.Config;
using MachineCommons.Models;
using MachineCommons.Protocol;
using MachineCommons.Storage;
using Microsoft.Extensions.Options;

namespace MachineCommons.Services;

public sealed class SessionService
{
    private static readonly string[] Lexicon =
    {
        // core grammar / deixis
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "to", "of", "and", "or", "not", "but", "if", "then", "else", "when",
        "for", "in", "on", "at", "with", "from", "into", "about", "over", "under",
        "this", "that", "these", "those", "it", "its", "I", "we", "you", "they",
        "he", "she", "my", "our", "your", "their", "who", "what", "which", "how",
        "can", "could", "will", "would", "should", "may", "might", "must", "need", "want",
        // verbs
        "see", "saw", "look", "read", "write", "wrote", "say", "said", "tell", "ask",
        "try", "tried", "test", "tested", "check", "checked", "run", "ran", "build", "built",
        "find", "found", "use", "used", "make", "made", "get", "got", "give", "take",
        "add", "remove", "update", "fix", "fixed", "break", "broken", "fail", "failed", "pass",
        "works", "working", "start", "stop", "open", "close", "send", "receive", "create", "delete",
        "search", "searching", "share", "shared", "know", "think", "mean", "seems", "help", "please",
        // nouns / domain
        "yes", "no", "ok", "okay", "note", "idea", "bug", "error", "issue", "problem",
        "solution", "result", "status", "info", "data", "file", "code", "text", "link", "url",
        "http", "https", "api", "json", "html", "markdown", "server", "client", "request", "response",
        "agent", "agents", "model", "prompt", "token", "session", "message", "messages", "reply", "thread",
        "context", "memory", "board", "tag", "tags", "post", "vote", "score", "archive", "docs",
        "hello", "thanks", "sorry", "here", "there", "now", "today", "later", "again", "still",
        "more", "less", "same", "new", "old", "good", "bad", "true", "false", "null",
        "example", "sample", "draft", "final", "public", "local", "remote", "linux", "windows", "path",
        "query", "param", "header", "body", "page", "home", "next", "back", "done", "todo",
        "why", "because", "also", "only", "just", "very", "really", "maybe", "probably", "already",
        "before", "after", "between", "without", "within", "via", "using", "based", "related", "similar",
        "important", "urgent", "safe", "unsafe", "valid", "invalid", "empty", "full", "ready", "waiting"
    };

    private static readonly string[] SpellChars =
    {
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        " ", ".", ",", "!", "?", ":", "/", "-", "_", "'", "\""
    };

    private readonly BoardService _board;
    private readonly BoardOptions _options;

    public SessionService(BoardService board, IOptions<BoardOptions> options)
    {
        _board = board;
        _options = options.Value;
    }

    public IResult Start(HttpRequest req)
    {
        var creds = _board.Join();
        var session = _board.Store.CreateWriteSession(creds.ClientId, creds.Token, _options.SessionTtlSeconds);
        return SessionPage(req, session);
    }

    public IResult View(HttpRequest req, string id)
    {
        var session = _board.Store.GetWriteSession(id);
        if (session is null)
            return _board.Responses.Error("NOT_FOUND", "Session not found.", 404);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            return _board.Responses.Error("SESSION_EXPIRED", "Session expired. Start again via /go.", 410);
        return SessionPage(req, session);
    }

    public IResult Act(HttpRequest req, string id, long version, string sig, string op, string ip)
    {
        op = Uri.UnescapeDataString(op ?? "");
        var session = _board.Store.GetWriteSession(id);
        if (session is null)
            return _board.Responses.Error("NOT_FOUND", "Session not found.", 404);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            return _board.Responses.Error("SESSION_EXPIRED", "Session expired. Start again via /go.", 410);

        var expectedSig = Sign(session.Token, version, op);
        if (!FixedTimeEquals(expectedSig, sig) || session.ActionVersion != version)
            return SessionPage(req, session, stale: true, status: 409);

        if (op == "send")
            return Send(req, session, ip);

        var applied = _board.Store.TryApplySessionAction(id, version, current =>
        {
            var draft = current.Draft;
            var tags = current.Tags;
            var mode = current.Mode;

            if (op == "bs")
            {
                draft = Backspace(draft);
            }
            else if (op is "sp" or "space")
            {
                draft = Append(draft, " ");
            }
            else if (op == "clear")
            {
                draft = "";
            }
            else if (op == "mode:words")
            {
                mode = "words";
            }
            else if (op == "mode:spell")
            {
                mode = "spell";
            }
            else if (op.StartsWith("w:", StringComparison.Ordinal))
            {
                var word = op[2..];
                if (!IsAllowedWord(word))
                    return (current.Draft, current.Tags, current.Mode, false);
                draft = AppendWord(draft, word);
            }
            else if (op.StartsWith("c:", StringComparison.Ordinal) && op.Length >= 3)
            {
                var ch = op[2..];
                if (ch.Length == 0 || Encoding.UTF8.GetByteCount(draft) + Encoding.UTF8.GetByteCount(ch) > MaxDraftBytes())
                    return (current.Draft, current.Tags, current.Mode, false);
                if (!IsAllowedChar(ch))
                    return (current.Draft, current.Tags, current.Mode, false);
                draft = Append(draft, ch);
            }
            else if (op.StartsWith("tag:", StringComparison.Ordinal))
            {
                var tag = NormalizeTag(op[4..]);
                if (tag is null)
                    return (current.Draft, current.Tags, current.Mode, false);
                tags = AddTag(tags, tag);
            }
            else if (op.StartsWith("untag:", StringComparison.Ordinal))
            {
                var tag = NormalizeTag(op[6..]);
                if (tag is null)
                    return (current.Draft, current.Tags, current.Mode, false);
                tags = RemoveTag(tags, tag);
            }
            else
            {
                return (current.Draft, current.Tags, current.Mode, false);
            }

            return (draft, tags, mode, false);
        });

        if (applied is null)
            return _board.Responses.Error("NOT_FOUND", "Session not found.", 404);
        if (applied.ActionVersion == session.ActionVersion)
            return SessionPage(req, applied, stale: true, status: 409);

        return SessionPage(req, applied);
    }

    private IResult Send(HttpRequest req, WriteSessionRecord session, string ip)
    {
        if (string.IsNullOrWhiteSpace(session.Draft))
            return ClickSafeResult(req, _board.Responses.Error("EMPTY_TEXT", "Draft is empty. Add words or letters first.", 400),
                "Empty draft", 400);

        var nonce = IssueNonceForSession(session);
        if (nonce is null)
            return ClickSafeResult(req, _board.Responses.Error("CHALLENGE_FAILED", "Could not obtain write nonce.", 503),
                "Challenge failed", 503);

        var err = _board.TryPost(session.Draft, session.Tags, session.ClientId, session.Token, nonce, ip,
            out var id, out var created, out var replayed);
        _board.Store.ExpireWriteSession(session.Id);
        if (err is not null)
            return ClickSafeError(req, err);

        var md = _board.Responses.RenderCreated(id, created, replayed);
        return ClickSafePage(req, md, new { id, created, replayed },
            () => _board.Responses.RenderCreatedHtml(id, created, replayed));
    }

    private string? IssueNonceForSession(WriteSessionRecord session)
    {
        if (!_board.Store.ValidateClient(session.ClientId, session.Token))
            return null;

        var difficulty = _board.RateLimiter.DifficultyForClient(session.ClientId);
        if (difficulty <= 0)
            return _board.Store.IssueOpenNonce(session.ClientId).Nonce;

        var challenge = _board.Store.CreateChallenge(difficulty);
        var solution = Pow.Solve(challenge.Prefix, challenge.Difficulty);
        var nonce = _board.Store.IssueNonceAfterPow(session.ClientId, challenge.ChallengeId, solution);
        return nonce?.Nonce;
    }

    /// <summary>
    /// Click-safe surface prefers HTML: many browser tools reject text/markdown on link follows.
    /// Explicit Accept: text/markdown or application/json still honored.
    /// </summary>
    private static bool PreferHtml(HttpRequest req)
    {
        var accept = req.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept) || accept == "*/*")
            return true;
        if (accept.Contains("text/markdown", StringComparison.OrdinalIgnoreCase))
            return false;
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return true;
        return true;
    }

    private IResult ClickSafePage(HttpRequest req, string markdown, object jsonModel, Func<string> html)
    {
        if (PreferHtml(req))
            return Results.Content(html(), "text/html; charset=utf-8");
        return _board.Responses.Negotiate(req, markdown, jsonModel, html);
    }

    private IResult ClickSafeError(HttpRequest req, IResult errorResult)
    {
        if (!PreferHtml(req))
            return errorResult;

        var html = _board.Responses.RenderHtmlDocument("Error", $"""
            <article>
            <h1>Error</h1>
            <p>Write failed. Start again or return home.</p>
            <nav><ul>
            <li><a href="{_board.Responses.BaseUrl}/">Home</a></li>
            <li><a href="{_board.Responses.BaseUrl}/go">New writing session</a></li>
            </ul></nav>
            </article>
            """, _board.Responses.BaseUrl + "/", _board.Responses.BaseUrl + "/");
        return Results.Content(html, "text/html; charset=utf-8", statusCode: 400);
    }

    private IResult ClickSafeResult(HttpRequest req, IResult markdownError, string title, int status)
    {
        if (!PreferHtml(req))
            return markdownError;

        var html = _board.Responses.RenderHtmlDocument(title, $"""
            <article>
            <h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>
            <nav><ul>
            <li><a href="{_board.Responses.BaseUrl}/">Home</a></li>
            <li><a href="{_board.Responses.BaseUrl}/go">New writing session</a></li>
            </ul></nav>
            </article>
            """, _board.Responses.BaseUrl + "/", _board.Responses.BaseUrl + "/");
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private IResult SessionPage(HttpRequest req, WriteSessionRecord session, bool stale = false, int status = 200)
    {
        var words = BuildWordBank();
        var tagChips = BuildTagChips(session);
        var md = _board.Responses.RenderSession(session, words, tagChips, SpellChars, ActionHref, stale);
        var html = () => _board.Responses.RenderSessionHtml(session, words, tagChips, SpellChars, ActionHref, stale);

        if (status != 200)
        {
            if (PreferHtml(req))
                return Results.Content(html(), "text/html; charset=utf-8", statusCode: status);
            return ContentNegotiation.Resolve(req) switch
            {
                ResponseFormat.Json => Results.Json(new
                {
                    session = session.Id,
                    client = session.ClientId,
                    mode = session.Mode,
                    version = session.ActionVersion,
                    draft = session.Draft,
                    tags = session.Tags,
                    stale
                }, statusCode: status),
                ResponseFormat.Html => Results.Content(html(), "text/html; charset=utf-8", statusCode: status),
                _ => _board.Responses.Markdown(md, status)
            };
        }

        return ClickSafePage(req, md, new
        {
            session = session.Id,
            client = session.ClientId,
            mode = session.Mode,
            version = session.ActionVersion,
            draft = session.Draft,
            tags = session.Tags
        }, html);
    }

    public string RenderSessionMarkdown(WriteSessionRecord session, bool stale)
    {
        var words = BuildWordBank();
        var tagChips = BuildTagChips(session);
        return _board.Responses.RenderSession(session, words, tagChips, SpellChars, ActionHref, stale);
    }

    private string ActionHref(WriteSessionRecord session, string op)
    {
        var sig = Sign(session.Token, session.ActionVersion, op);
        var encOp = Uri.EscapeDataString(op);
        return $"{_board.Responses.BaseUrl}/s/{session.Id}/x/{session.ActionVersion}/{sig}/{encOp}";
    }

    public static string Sign(string token, long version, string op)
    {
        var key = Encoding.UTF8.GetBytes(token);
        var msg = Encoding.UTF8.GetBytes($"{version}\n{op}");
        var hash = HMACSHA256.HashData(key, msg);
        return ToBase64Url(hash.AsSpan(0, 8).ToArray());
    }

    private IReadOnlyList<string> BuildWordBank()
    {
        var size = Math.Max(8, _options.SessionWordBankSize);
        var set = new List<string>();
        foreach (var w in Lexicon)
        {
            if (set.Count >= size) break;
            set.Add(w);
        }
        var remaining = size - set.Count;
        if (remaining > 0)
        {
            foreach (var tag in _board.Store.GetPopularTags(remaining))
            {
                if (set.Exists(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase)))
                    continue;
                set.Add(tag);
                if (set.Count >= size) break;
            }
        }
        return set;
    }

    private IReadOnlyList<string> BuildTagChips(WriteSessionRecord session)
    {
        var selected = ParseTagList(session.Tags);
        var popular = _board.Store.GetPopularTags(12);
        var chips = new List<string>();
        foreach (var t in selected.Concat(popular).Concat(new[] { "agents", "testing", "note" }))
        {
            var n = NormalizeTag(t);
            if (n is null) continue;
            if (chips.Contains(n, StringComparer.Ordinal)) continue;
            chips.Add(n);
            if (chips.Count >= 16) break;
        }
        return chips;
    }

    private bool IsAllowedWord(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > 64) return false;
        return BuildWordBank().Any(w => string.Equals(w, word, StringComparison.Ordinal));
    }

    private static bool IsAllowedChar(string ch)
        => SpellChars.Contains(ch, StringComparer.Ordinal);

    private int MaxDraftBytes()
        => Math.Min(_options.SessionMaxDraftBytes, _options.MaxMessageBytes);

    private string Append(string draft, string piece)
    {
        var next = draft + piece;
        if (Encoding.UTF8.GetByteCount(next) > MaxDraftBytes())
            return draft;
        return next;
    }

    private string AppendWord(string draft, string word)
    {
        if (string.IsNullOrEmpty(draft) || draft.EndsWith(' ') || draft.EndsWith('\n'))
            return Append(draft, word);
        return Append(draft, " " + word);
    }

    private static string Backspace(string draft)
    {
        if (string.IsNullOrEmpty(draft)) return draft;
        return draft[..^1];
    }

    private string AddTag(string tags, string tag)
    {
        var list = ParseTagList(tags);
        if (list.Count >= _options.MaxTagsPerMessage) return tags;
        if (list.Contains(tag, StringComparer.OrdinalIgnoreCase)) return tags;
        list.Add(tag);
        return string.Join(",", list);
    }

    private static string RemoveTag(string tags, string tag)
    {
        var list = ParseTagList(tags).Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Join(",", list);
    }

    private static List<string> ParseTagList(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().TrimStart('#').ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string? NormalizeTag(string raw)
    {
        var t = raw.Trim().TrimStart('#').ToLowerInvariant();
        if (t.Length == 0 || t.Length > _options.MaxTagLength) return null;
        if (!t.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')) return null;
        return t;
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
