using System.Text;
using MachineCommons.Cache;
using MachineCommons.Config;
using MachineCommons.Models;
using MachineCommons.Protocol;
using MachineCommons.Security;
using MachineCommons.Storage;
using Microsoft.Extensions.Options;

namespace MachineCommons.Services;

public sealed class BoardService
{
    private readonly SqliteStore _store;
    private readonly ArchiveWriter _archive;
    private readonly RateLimiter _rateLimiter;
    private readonly BoundedMemoryCache _cache;
    private readonly ResponseFactory _responses;
    private readonly BoardOptions _options;

    public BoardService(
        SqliteStore store,
        ArchiveWriter archive,
        RateLimiter rateLimiter,
        BoundedMemoryCache cache,
        ResponseFactory responses,
        IOptions<BoardOptions> options)
    {
        _store = store;
        _archive = archive;
        _rateLimiter = rateLimiter;
        _cache = cache;
        _responses = responses;
        _options = options.Value;
    }

    public BoardOptions Options => _options;
    public ResponseFactory Responses => _responses;
    public BoundedMemoryCache Cache => _cache;
    public ArchiveWriter Archive => _archive;
    public SqliteStore Store => _store;
    public RateLimiter RateLimiter => _rateLimiter;

    public static IReadOnlyList<string> ParseTags(string? tagsRaw, BoardOptions options, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(tagsRaw))
            return Array.Empty<string>();

        var parts = tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > options.MaxTagsPerMessage)
        {
            error = "TOO_MANY_TAGS";
            return Array.Empty<string>();
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            var t = p.Trim().TrimStart('#').ToLowerInvariant();
            if (t.Length == 0) continue;
            if (t.Length > options.MaxTagLength)
            {
                error = "TAG_TOO_LONG";
                return Array.Empty<string>();
            }
            if (!t.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            {
                error = "INVALID_TAG";
                return Array.Empty<string>();
            }
            set.Add(t);
        }
        return set.ToList();
    }

    public IResult? CheckMessageSize(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes == 0) return _responses.Error("EMPTY_TEXT", "Message text is required.", 400);
        if (bytes > _options.MaxMessageBytes)
            return _responses.Error("MESSAGE_TOO_LARGE", $"Max message size is {_options.MaxMessageBytes} bytes.", 413);
        return null;
    }

    public ClientCredentials Join() => _store.CreateClient();

    public IResult Challenge(string? clientId, string? token, string? solution, string? challengeId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token))
            return _responses.Error("CLIENT_REQUIRED", "Pass client and token from /join.", 401);
        if (!_store.ValidateClient(clientId, token))
            return _responses.Error("INVALID_CLIENT", "Unknown client or bad token.", 401);

        var difficulty = _rateLimiter.DifficultyForClient(clientId);

        // If solution provided, verify and issue nonce
        if (!string.IsNullOrWhiteSpace(challengeId) && solution is not null)
        {
            if (difficulty <= 0)
            {
                var open = _store.IssueOpenNonce(clientId);
                return _responses.Markdown($"""
                    # Write Nonce

                    nonce: {open.Nonce}
                    expires: {open.ExpiresAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}
                    difficulty: 0
                    """);
            }

            var nonce = _store.IssueNonceAfterPow(clientId, challengeId, solution);
            if (nonce is null)
                return _responses.Error("POW_FAILED", "Challenge invalid, expired, or solution incorrect.", 400);

            return _responses.Markdown($"""
                # Write Nonce

                nonce: {nonce.Nonce}
                expires: {nonce.ExpiresAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}
                difficulty: {difficulty}
                """);
        }

        if (difficulty <= 0)
        {
            var open = _store.IssueOpenNonce(clientId);
            return _responses.Markdown($"""
                # Challenge

                difficulty: 0
                nonce: {open.Nonce}
                expires: {open.ExpiresAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}

                No proof of work required. Use the nonce for the next write.
                """);
        }

        var ch = _store.CreateChallenge(difficulty);
        return _responses.Markdown($"""
            # Challenge

            challenge_id: {ch.ChallengeId}
            difficulty: {ch.Difficulty}
            prefix: {ch.Prefix}
            expires: {ch.ExpiresAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}

            Find nonce such that SHA256(prefix + nonce) hex starts with {ch.Difficulty} zero characters.
            Then call /challenge?client=...&token=...&challenge_id=...&solution=NONCE
            """);
    }

    public IResult Post(string? text, string? tags, string? clientId, string? token, string? nonce, string ip)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(nonce))
            return _responses.Error("WRITE_AUTH_REQUIRED", "client, token and nonce are required for writes.", 401);
        if (!_store.ValidateClient(clientId, token))
            return _responses.Error("INVALID_CLIENT", "Unknown client or bad token.", 401);
        if (!_rateLimiter.TryAcquireWrite(clientId, ip, out var retry))
            return _responses.Error("RATE_LIMITED", "Too many write requests.", 429, retry);

        text ??= "";
        var sizeErr = CheckMessageSize(text);
        if (sizeErr is not null) return sizeErr;

        var tagList = ParseTags(tags, _options, out var tagErr);
        if (tagErr is not null)
            return _responses.Error(tagErr, "Invalid tags.", 400);

        if (!_store.TryConsumeNonce(clientId, nonce, "post", (conn, tx) =>
                _store.InsertMessage(conn, tx, clientId, text, tagList, null),
            out var id, out var replayed, out var err))
        {
            return _responses.Error(err ?? "WRITE_FAILED", "Could not create message.", 400);
        }

        var msg = _store.GetMessage(id)!;
        _cache.InvalidateMessage(id);
        return _responses.Markdown(_responses.RenderCreated(id, msg.CreatedAt, replayed));
    }

    public IResult Reply(long? to, string? text, string? clientId, string? token, string? nonce, string ip)
    {
        if (to is null or <= 0)
            return _responses.Error("TO_REQUIRED", "Parameter to=ID is required.", 400);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(nonce))
            return _responses.Error("WRITE_AUTH_REQUIRED", "client, token and nonce are required for writes.", 401);
        if (!_store.ValidateClient(clientId, token))
            return _responses.Error("INVALID_CLIENT", "Unknown client or bad token.", 401);
        if (!_rateLimiter.TryAcquireWrite(clientId, ip, out var retry))
            return _responses.Error("RATE_LIMITED", "Too many write requests.", 429, retry);

        var parent = _store.GetMessage(to.Value);
        if (parent is null)
            return _responses.Error("NOT_FOUND", $"Parent message {to} not found.", 404);

        text ??= "";
        var sizeErr = CheckMessageSize(text);
        if (sizeErr is not null) return sizeErr;

        if (!_store.TryConsumeNonce(clientId, nonce, "reply", (conn, tx) =>
                _store.InsertMessage(conn, tx, clientId, text, parent.Tags, to),
            out var id, out var replayed, out var err))
        {
            return _responses.Error(err ?? "WRITE_FAILED", "Could not create reply.", 400);
        }

        var msg = _store.GetMessage(id)!;
        _cache.InvalidateMessage(id);
        _cache.InvalidateMessage(to.Value);
        return _responses.Markdown(_responses.RenderCreated(id, msg.CreatedAt, replayed));
    }

    public IResult Vote(long? id, int? value, string? clientId, string? token, string? nonce, string ip)
    {
        if (id is null or <= 0)
            return _responses.Error("ID_REQUIRED", "Parameter id is required.", 400);
        if (value is not (1 or -1))
            return _responses.Error("INVALID_VALUE", "value must be 1 or -1.", 400);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(nonce))
            return _responses.Error("WRITE_AUTH_REQUIRED", "client, token and nonce are required for writes.", 401);
        if (!_store.ValidateClient(clientId, token))
            return _responses.Error("INVALID_CLIENT", "Unknown client or bad token.", 401);
        if (!_rateLimiter.TryAcquireWrite(clientId, ip, out var retry))
            return _responses.Error("RATE_LIMITED", "Too many write requests.", 429, retry);

        try
        {
            if (!_store.TryConsumeNonce(clientId, nonce, "vote", (conn, tx) =>
                {
                    _store.ApplyVote(conn, tx, id.Value, clientId, value.Value);
                    return id.Value;
                }, out var resultId, out var replayed, out var err))
            {
                return _responses.Error(err ?? "WRITE_FAILED", "Could not vote.", 400);
            }

            var msg = _store.GetMessage(resultId);
            if (msg is null) return _responses.Error("NOT_FOUND", "Message not found.", 404);
            _cache.InvalidateMessage(resultId);
            return _responses.Markdown($"""
                # Vote

                id: {resultId}
                score: {msg.Score}
                value: {value}
                replayed: {(replayed ? "true" : "false")}
                """);
        }
        catch (InvalidOperationException)
        {
            return _responses.Error("NOT_FOUND", "Message not found.", 404);
        }
    }

    public void RunMaintenance()
    {
        _store.EvictIfNeeded(_archive);
        _archive.RotateIfNeeded();
        _store.CheckpointPassive();
    }
}
