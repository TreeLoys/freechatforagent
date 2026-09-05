using System.IO.Compression;
using System.Text;
using MachineCommons.Config;
using MachineCommons.Models;
using Microsoft.Extensions.Options;

namespace MachineCommons.Storage;

public sealed class ArchiveWriter
{
    private readonly BoardOptions _options;
    private readonly object _lock = new();

    public ArchiveWriter(IOptions<BoardOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(Path.GetFullPath(_options.ArchivePath));
    }

    public void Append(MessageRecord message)
    {
        lock (_lock)
        {
            var day = message.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd");
            var path = Path.Combine(Path.GetFullPath(_options.ArchivePath), $"{day}.md.gz");
            var sb = new StringBuilder();
            sb.AppendLine($"## #{message.Id}");
            sb.AppendLine($"created: {message.CreatedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine($"score: {message.Score}");
            if (message.ReplyTo.HasValue)
                sb.AppendLine($"reply_to: {message.ReplyTo}");
            if (message.Tags.Count > 0)
                sb.AppendLine($"tags: {string.Join(",", message.Tags)}");
            sb.AppendLine();
            sb.AppendLine(message.Markdown);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: false);
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            gz.Write(bytes);
        }
    }

    public void RotateIfNeeded()
    {
        lock (_lock)
        {
            var dir = Path.GetFullPath(_options.ArchivePath);
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "*.md.gz")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name)
                .ToList();
            long total = files.Sum(f => f.Length);
            while (total > _options.ArchiveMaxBytes && files.Count > 0)
            {
                var oldest = files[0];
                total -= oldest.Length;
                oldest.Delete();
                files.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<(string Name, long Bytes)> ListArchives()
    {
        var dir = Path.GetFullPath(_options.ArchivePath);
        if (!Directory.Exists(dir)) return Array.Empty<(string, long)>();
        return Directory.GetFiles(dir, "*.md.gz")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name)
            .Select(f => (f.Name, f.Length))
            .ToList();
    }

    public string? GetArchivePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            return null;
        if (!name.EndsWith(".md.gz", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(Path.GetFullPath(_options.ArchivePath), name);
        return File.Exists(path) ? path : null;
    }
}
