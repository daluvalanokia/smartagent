using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>
/// Mode c: fetches a live website, captures the rendered HTML plus same-site
/// pages (limited crawl) so erratic behavior (JS errors, dead links, broken
/// forms) can be analyzed.
/// </summary>
public sealed partial class WebsiteSourceProvider(HttpClient http) : ISourceProvider
{
    public SourceType SourceType => SourceType.Website;

    private const int MaxPages = 12;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<SourceSnapshot> FetchAsync(SourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            throw new ArgumentException("A website URL is required.", nameof(request));

        var root = NormalizeUrl(request.Url);
        var baseUri = new Uri(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(root);
        visited.Add(root);

        var files = new List<SourceFile>();
        var warnings = new List<string>();

        while (queue.Count > 0 && files.Count < MaxPages)
        {
            var url = queue.Dequeue();
            string html;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
                cts.CancelAfter(Timeout);
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    warnings.Add($"{url} returned {(int)resp.StatusCode}");
                    continue;
                }
                var media = resp.Content.Headers.ContentType?.MediaType ?? "text/html";
                if (!media.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
                html = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                warnings.Add($"{url} unreachable: {ex.Message}");
                continue;
            }

            var path = files.Count == 0
                ? "index.html"
                : Uri.UnescapeDataString(new Uri(url).AbsolutePath).Trim('/').Replace('/', '_') + ".html";

            files.Add(new SourceFile
            {
                Path = string.IsNullOrWhiteSpace(path) ? "index.html" : path,
                Content = html
            });

            foreach (var link in ExtractLinks(html, baseUri))
            {
                var normalized = NormalizeUrl(link);
                if (visited.Add(normalized) && new Uri(normalized).Host == baseUri.Host && files.Count + queue.Count < MaxPages)
                    queue.Enqueue(normalized);
            }
        }

        if (files.Count == 0)
            throw new InvalidOperationException($"Could not fetch any content from '{root}'. {string.Join(" ", warnings)}");

        return new SourceSnapshot
        {
            SourceType = SourceType.Website,
            SourceName = baseUri.Host,
            SourceDetail = $"{files.Count} page(s) crawled from {root}",
            Files = files
        };
    }

    internal static string NormalizeUrl(string raw)
    {
        var t = raw.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            t = "https://" + t;
        return t.TrimEnd('/');
    }

    [GeneratedRegex("""<a\b[^>]*?href\s*=\s*["']([^"'#]+)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HrefRegex();

    internal static IEnumerable<string> ExtractLinks(string html, Uri baseUri)
    {
        foreach (var m in HrefRegex().Matches(html).Cast<Match>())
        {
            var href = m.Groups[1].Value;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Uri.TryCreate(baseUri, href, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                yield return abs.ToString();
        }
    }
}
