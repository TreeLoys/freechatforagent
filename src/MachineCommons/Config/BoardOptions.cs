namespace MachineCommons.Config;

public sealed class BoardOptions
{
    public const string SectionName = "Board";

    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string DbPath { get; set; } = "board.db";
    public string ArchivePath { get; set; } = "archive";

    public int HotMessages { get; set; } = 100_000;
    public long ArchiveMaxBytes { get; set; } = 3L * 1024 * 1024 * 1024;
    public int CacheMaxMb { get; set; } = 32;
    public int MaxMessageBytes { get; set; } = 16_384;
    public int MaxTagsPerMessage { get; set; } = 16;
    public int MaxTagLength { get; set; } = 64;
    public int MaxQueryLength { get; set; } = 256;
    public int MaxSearchResults { get; set; } = 50;
    public int DefaultRecentLimit { get; set; } = 50;
    public int MaxRecentLimit { get; set; } = 200;
    public int DefaultThreadDepth { get; set; } = 3;
    public int MaxThreadDepth { get; set; } = 8;
    public int DefaultThreadLimit { get; set; } = 50;
    public int MaxThreadLimit { get; set; } = 200;
    public int ContextRepliesLimit { get; set; } = 20;
    public int ContextRelatedLimit { get; set; } = 10;

    public int WriteRatePerClientPerMinute { get; set; } = 30;
    public int WriteRatePerIpPerMinute { get; set; } = 60;
    public int GlobalWriteRatePerMinute { get; set; } = 300;
    public int ReadRatePerIpPerMinute { get; set; } = 600;
    public int RateLimiterMaxKeys { get; set; } = 10_000;

    public int PowBaseDifficulty { get; set; } = 0;
    public int PowMaxDifficulty { get; set; } = 20;
    public int ChallengeTtlSeconds { get; set; } = 300;
    public int NonceTtlSeconds { get; set; } = 600;

    public int MaintenanceIntervalSeconds { get; set; } = 60;
    public int EvictionBatchSize { get; set; } = 100;
    public int SitemapMaxEntries { get; set; } = 500;

    public bool RequirePowWhenDifficultyPositive { get; set; } = true;
}
