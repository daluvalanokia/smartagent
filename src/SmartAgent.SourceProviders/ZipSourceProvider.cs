using System.IO.Compression;
using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>Mode a: reads app source from an uploaded ZIP archive.</summary>
public sealed class ZipSourceProvider : ISourceProvider
{
    public SourceType SourceType => SourceType.ZipArchive;

    public Task<SourceSnapshot> FetchAsync(SourceRequest request)
    {
        if (request.ZipStream is null)
            throw new ArgumentException("A ZIP stream is required for this source.", nameof(request));

        var files = new List<SourceFile>();
        var skipped = 0;
        long total = 0;

        using var archive = new ZipArchive(request.ZipStream, ZipArchiveMode.Read, leaveOpen: true);

        // Strip the single common top folder ("myapp-main/") so paths are stable.
        string? rootStrip = null;
        var topLevel = archive.Entries
            .Where(e => e.Length > 0)
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();
        var firstSegments = topLevel.Select(p => p.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (firstSegments.Count == 1)
        {
            var candidate = firstSegments[0];
            if (topLevel.All(p => p.StartsWith(candidate + "/", StringComparison.Ordinal)))
                rootStrip = candidate + "/";
        }

        foreach (var entry in archive.Entries.Where(e => e.Length > 0))
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith("/", StringComparison.Ordinal)) continue;
            if (SourceProviderCommon.IsSkippedPath(path)) { skipped++; continue; }

            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();

            if (rootStrip is not null && path.StartsWith(rootStrip, StringComparison.Ordinal))
                path = path[rootStrip.Length..];

            if (files.Count >= SourceProviderCommon.MaxFiles) { skipped++; continue; }
            if (total + bytes.Length > SourceProviderCommon.MaxTotalBytes) { skipped++; continue; }

            var file = SourceProviderCommon.TryRead(path, bytes, out _);
            if (file is null) { skipped++; continue; }

            files.Add(file);
            total += bytes.Length;
        }

        if (files.Count == 0)
            throw new InvalidOperationException("The ZIP archive contained no analyzable source files.");

        var snapshot = new SourceSnapshot
        {
            SourceType = SourceType.ZipArchive,
            SourceName = "Uploaded archive",
            SourceDetail = $"{files.Count} files, {total / 1024.0:F0} KB analyzed ({skipped} skipped)",
            Files = files
        };
        return Task.FromResult(snapshot);
    }
}
