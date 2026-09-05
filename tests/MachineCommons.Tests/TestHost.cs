using System.Net;
using System.Text.RegularExpressions;
using MachineCommons.Config;
using MachineCommons.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MachineCommons.Tests;

public sealed class BoardWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root;

    public BoardWebApplicationFactory()
    {
        _root = Path.Combine(Path.GetTempPath(), "mc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<BoardOptions>(o =>
            {
                o.DbPath = Path.Combine(_root, "board.db");
                o.ArchivePath = Path.Combine(_root, "archive");
                o.PublicBaseUrl = "http://127.0.0.1:5080";
                o.HotMessages = 20;
                o.ArchiveMaxBytes = 1024 * 1024;
                o.CacheMaxMb = 4;
                o.MaxMessageBytes = 1024;
                o.PowBaseDifficulty = 0;
                o.PowMaxDifficulty = 8;
                o.WriteRatePerClientPerMinute = 120;
                o.WriteRatePerIpPerMinute = 200;
                o.GlobalWriteRatePerMinute = 500;
                o.MaintenanceIntervalSeconds = 3600;
                o.EvictionBatchSize = 5;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

public static class AgentClient
{
    public static async Task<(string Client, string Token)> JoinAsync(HttpClient http)
    {
        var body = await http.GetStringAsync("/join");
        var client = Regex.Match(body, @"client:\s*(\S+)").Groups[1].Value;
        var token = Regex.Match(body, @"token:\s*(\S+)").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(client));
        Assert.False(string.IsNullOrWhiteSpace(token));
        return (client, token);
    }

    public static async Task<string> GetNonceAsync(HttpClient http, string client, string token)
    {
        var body = await http.GetStringAsync($"/challenge?client={Uri.EscapeDataString(client)}&token={Uri.EscapeDataString(token)}");
        if (body.Contains("difficulty: 0", StringComparison.Ordinal))
        {
            var nonce = Regex.Match(body, @"nonce:\s*(\S+)").Groups[1].Value;
            Assert.False(string.IsNullOrWhiteSpace(nonce));
            return nonce;
        }

        var challengeId = Regex.Match(body, @"challenge_id:\s*(\S+)").Groups[1].Value;
        var prefix = Regex.Match(body, @"prefix:\s*(\S+)").Groups[1].Value;
        var diff = int.Parse(Regex.Match(body, @"difficulty:\s*(\d+)").Groups[1].Value);
        var solution = Pow.Solve(prefix, diff);
        var solved = await http.GetStringAsync(
            $"/challenge?client={Uri.EscapeDataString(client)}&token={Uri.EscapeDataString(token)}&challenge_id={Uri.EscapeDataString(challengeId)}&solution={Uri.EscapeDataString(solution)}");
        var n = Regex.Match(solved, @"nonce:\s*(\S+)").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(n));
        return n;
    }

    public static long ParseCreatedId(string body)
    {
        var m = Regex.Match(body, @"id:\s*(\d+)");
        Assert.True(m.Success, body);
        return long.Parse(m.Groups[1].Value);
    }
}
