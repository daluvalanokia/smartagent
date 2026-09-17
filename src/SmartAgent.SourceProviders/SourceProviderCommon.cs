using System.Text;
using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>Shared caps + filters so every provider produces bounded snapshots.</summary>
internal static class SourceProviderCommon
{
    public const int MaxFiles = 400;
    public const long MaxFileBytes = 512 * 1024;          // 512 KB per file
    public const long MaxTotalBytes = 8 * 1024 * 1024;    // 8 MB overall

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", "packages", "dist", "build", "vendor", "bower_components"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".razor", ".js", ".ts", ".jsx", ".tsx", ".vb", ".fs", ".py", ".java", ".kt",
        ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".m", ".sql",
        ".json", ".xml", ".yml", ".yaml", ".config", ".csproj", ".sln", ".props", ".targets",
        ".html", ".htm", ".css", ".scss", ".less", ".md", ".txt", ".env", ".ini", ".toml", ".svg"
    };

    /// <summary>Heuristic text check for extensionless files (Dockerfile, Makefile, etc.).</summary>
    private static bool LooksTextual(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes.Length > 2048 ? bytes[..2048] : bytes;
        int suspicious = 0;
        foreach (var b in sample)
        {
            if (b == 0) return false;
            if (b is < 9 or (> 13 and < 32)) suspicious++;
        }
        return sample.Length == 0 || suspicious * 100 / sample.Length < 5;
    }

    /// <summary>Decodes a candidate file into a <see cref="SourceFile"/>, or null if skipped.</summary>
    public static SourceFile? TryRead(string path, ReadOnlySpan<byte> bytes, out string reason)
    {
        reason = "included";
        if (bytes.Length == 0) { reason = "empty"; return null; }
        if (bytes.Length > MaxFileBytes) { reason = "too-large"; return null; }

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        bool textual = TextExtensions.Contains(ext) || LooksTextual(bytes);
        if (!textual) { reason = "binary"; return null; }

        return new SourceFile
        {
            Path = path.Replace('\\', '/'),
            Content = Encoding.UTF8.GetString(bytes)
        };
    }

    public static bool IsSkippedPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => SkippedDirs.Contains(p));
    }
}
