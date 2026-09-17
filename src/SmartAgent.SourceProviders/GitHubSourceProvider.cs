using System.Formats.Tar;
using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>Mode b: fetches the default branch tarball of a GitHub repository.</summary>
public sealed class GitHubSourceProvider(HttpClient http) : ISourceProvider
{
    public SourceType SourceType => SourceType.GitHub;

    public async Task<SourceSnapshot> FetchAsync(SourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            throw new ArgumentException("A GitHub repository URL is required.", nameof(request));

        var (owner, repo, branch) = Parse(request.Url);
        var url = branch is null
            ? $"https://codeload.github.com/{owner}/{repo}/tar.gz/HEAD"
            : $"https://codeload.github.com/{owner}/{repo}/tar.gz/{Uri.EscapeDataString(branch)}";

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
            httpReq.Headers.Add("Authorization", $"Bearer {request.GitHubToken}");

        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, request.CancellationToken);
        resp.EnsureSuccessStatusCode();

        await using var tarStream = await resp.Content.ReadAsStreamAsync(request.CancellationToken);
        var files = await ExtractTarGzAsync(tarStream, $"{owner}-{repo}");
        if (files.Count == 0)
            throw new InvalidOperationException($"No analyzable files found in {owner}/{repo}.");

        return new SourceSnapshot
        {
            SourceType = SourceType.GitHub,
            SourceName = $"{owner}/{repo}",
            SourceDetail = $"branch {(branch ?? "HEAD")}, {files.Count} files analyzed",
            Files = files
        };
    }

    public static (string Owner, string Repo, string? Branch) Parse(string raw)
    {
        // Accepts: https://github.com/owner/repo, .../tree/branch, owner/repo, github.com/owner/repo
        var trimmed = raw.Trim().TrimEnd('/');
        string path;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
        {
            if (!u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{raw}' is not a GitHub repository URL.", nameof(raw));
            path = u.AbsolutePath;
        }
        else if (trimmed.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            path = "/" + trimmed["github.com/".Length..];
        else
            path = "/" + trimmed;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"'{raw}' is not a GitHub repository URL.", nameof(raw));

        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        string? branch = null;
        if (parts.Length >= 4 && parts[2] is "tree" or "blob") branch = string.Join("/", parts[3..]);
        return (owner, repo, branch);
    }

    private static async Task<List<SourceFile>> ExtractTarGzAsync(Stream tarGz, string repoSlug)
    {
        var files = new List<SourceFile>();
        long total = 0;

        await using var gzip = new System.IO.Compression.GZipStream(tarGz, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (await tar.GetNextEntryAsync(copyData: false) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;

            var path = (entry.Name ?? string.Empty).Replace('\\', '/');
            if (SourceProviderCommon.IsSkippedPath(path)) continue;

            // Strip the GitHub root folder ("owner-repo-<sha>/").
            var slash = path.IndexOf('/');
            if (slash > 0 && path[..slash].StartsWith(repoSlug, StringComparison.OrdinalIgnoreCase))
                path = path[(slash + 1)..];
            if (path.Length == 0) continue;

            if (files.Count >= SourceProviderCommon.MaxFiles) break;
            if (total + entry.Length > SourceProviderCommon.MaxTotalBytes) break;

            using var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            var file = SourceProviderCommon.TryRead(path, bytes, out _);
            if (file is null) continue;

            files.Add(file);
            total += bytes.Length;
        }
        return files;
    }
}
