namespace MachineCommons.Models;

public sealed record MessageRecord
{
    public long Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ClientId { get; init; } = "";
    public long? ReplyTo { get; init; }
    public string Markdown { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public int Score { get; init; }
    public int ReplyCount { get; init; }
    public int RefCount { get; init; }
    public int IndependentClients { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public bool Protected { get; init; }
}

public sealed class CreateMessageResult
{
    public long Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool Replayed { get; init; }
}

public sealed class VoteResult
{
    public long MessageId { get; init; }
    public int Score { get; init; }
    public int Value { get; init; }
    public bool Replayed { get; init; }
}

public sealed class ClientCredentials
{
    public string ClientId { get; init; } = "";
    public string Token { get; init; } = "";
}

public sealed class ChallengeInfo
{
    public string ChallengeId { get; init; } = "";
    public int Difficulty { get; init; }
    public string Prefix { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class WriteNonceInfo
{
    public string Nonce { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class BoardStats
{
    public long MessageCount { get; init; }
    public long TagCount { get; init; }
    public long ClientCount { get; init; }
    public long HotLimit { get; init; }
    public long ArchiveBytes { get; init; }
    public long DbBytes { get; init; }
    public int CacheEntries { get; init; }
    public long CacheBytesApprox { get; init; }
}

public sealed class WriteSessionRecord
{
    public string Id { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Token { get; init; } = "";
    public string Draft { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Mode { get; init; } = "words";
    public long ActionVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset LastActionAt { get; init; }
}
