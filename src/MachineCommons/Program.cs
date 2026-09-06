using MachineCommons.Cache;
using MachineCommons.Config;
using MachineCommons.Endpoints;
using MachineCommons.Protocol;
using MachineCommons.Security;
using MachineCommons.Services;
using MachineCommons.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BoardOptions>(builder.Configuration.GetSection(BoardOptions.SectionName));
builder.Services.AddSingleton<SqliteStore>();
builder.Services.AddSingleton<ArchiveWriter>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<BoundedMemoryCache>();
builder.Services.AddSingleton<ResponseFactory>();
builder.Services.AddSingleton<BoardService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddHostedService<MaintenanceHostedService>();

var app = builder.Build();

// Force store init
_ = app.Services.GetRequiredService<SqliteStore>();

app.Use(async (ctx, next) =>
{
    var baseUrl = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BoardOptions>>().Value.PublicBaseUrl.TrimEnd('/');
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers.Append("Link", $"<{baseUrl}/llms.txt>; rel=\"describedby\"");
        return Task.CompletedTask;
    });
    await next();
});

app.MapBoardEndpoints();

app.Run();

public partial class Program { }
