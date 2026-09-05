using System.Net;
using MachineCommons.Config;
using MachineCommons.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MachineCommons.Tests;

public class RateLimitAndPowTests
{
    [Fact]
    public async Task Rate_Limit_Returns_429()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mc-rl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<BoardOptions>(o =>
                {
                    o.DbPath = Path.Combine(root, "board.db");
                    o.ArchivePath = Path.Combine(root, "archive");
                    o.WriteRatePerClientPerMinute = 2;
                    o.WriteRatePerIpPerMinute = 100;
                    o.GlobalWriteRatePerMinute = 100;
                    o.PowBaseDifficulty = 0;
                    o.MaintenanceIntervalSeconds = 3600;
                });
            });
        });

        var http = factory.CreateClient();
        var (client, token) = await AgentClient.JoinAsync(http);

        async Task<HttpResponseMessage> PostOnce()
        {
            var nonce = await AgentClient.GetNonceAsync(http, client, token);
            return await http.GetAsync($"/post?text=rl-{Guid.NewGuid():N}&client={client}&token={token}&nonce={nonce}");
        }

        Assert.Equal(HttpStatusCode.OK, (await PostOnce()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostOnce()).StatusCode);
        var limited = await PostOnce();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var body = await limited.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", body);
    }

    [Fact]
    public async Task Pow_Challenge_With_Difficulty()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mc-pow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<BoardOptions>(o =>
                {
                    o.DbPath = Path.Combine(root, "board.db");
                    o.ArchivePath = Path.Combine(root, "archive");
                    o.PowBaseDifficulty = 2;
                    o.PowMaxDifficulty = 4;
                    o.MaintenanceIntervalSeconds = 3600;
                });
            });
        });

        var http = factory.CreateClient();
        var (client, token) = await AgentClient.JoinAsync(http);
        var challenge = await http.GetStringAsync($"/challenge?client={client}&token={token}");
        Assert.Contains("challenge_id:", challenge);
        Assert.Contains("difficulty:", challenge);

        var challengeId = System.Text.RegularExpressions.Regex.Match(challenge, @"challenge_id:\s*(\S+)").Groups[1].Value;
        var prefix = System.Text.RegularExpressions.Regex.Match(challenge, @"prefix:\s*(\S+)").Groups[1].Value;
        var diff = int.Parse(System.Text.RegularExpressions.Regex.Match(challenge, @"difficulty:\s*(\d+)").Groups[1].Value);
        var solution = Pow.Solve(prefix, diff);
        var nonceBody = await http.GetStringAsync(
            $"/challenge?client={client}&token={token}&challenge_id={challengeId}&solution={solution}");
        Assert.Contains("nonce:", nonceBody);

        var nonce = System.Text.RegularExpressions.Regex.Match(nonceBody, @"nonce:\s*(\S+)").Groups[1].Value;
        var created = await http.GetStringAsync($"/post?text=pow-ok&tags=pow&client={client}&token={token}&nonce={nonce}");
        Assert.Contains("# Created", created);
    }

    [Fact]
    public async Task Protected_Message_Survives_Eviction_Preference()
    {
        using var factory = new BoardWebApplicationFactory();
        var http = factory.CreateClient();
        var (client, token) = await AgentClient.JoinAsync(http);
        var nonce = await AgentClient.GetNonceAsync(http, client, token);
        var created = await http.GetStringAsync($"/post?text=keep-me-protected&tags=keep&client={client}&token={token}&nonce={nonce}");
        var id = AgentClient.ParseCreatedId(created);

        var store = factory.Services.GetRequiredService<SqliteStore>();
        store.MarkProtected(id);

        for (var i = 0; i < 40; i++)
        {
            var n = await AgentClient.GetNonceAsync(http, client, token);
            await http.GetStringAsync($"/post?text=noise-{i}&tags=noise&client={client}&token={token}&nonce={n}");
        }

        var board = factory.Services.GetRequiredService<Services.BoardService>();
        board.RunMaintenance();
        board.RunMaintenance();
        board.RunMaintenance();

        var msg = store.GetMessage(id);
        Assert.NotNull(msg);
        Assert.True(msg!.Protected);
    }

    [Fact]
    public void Archive_Rotation_Deletes_Oldest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "archive"));
        var options = Microsoft.Extensions.Options.Options.Create(new BoardOptions
        {
            ArchivePath = Path.Combine(root, "archive"),
            ArchiveMaxBytes = 200
        });
        var archive = new ArchiveWriter(options);
        File.WriteAllBytes(Path.Combine(root, "archive", "2020-01-01.md.gz"), new byte[150]);
        File.WriteAllBytes(Path.Combine(root, "archive", "2020-01-02.md.gz"), new byte[150]);
        archive.RotateIfNeeded();
        var files = Directory.GetFiles(Path.Combine(root, "archive"), "*.md.gz");
        Assert.True(files.Length <= 1);
        Directory.Delete(root, true);
    }
}
