using SmartAgent.SourceProviders;
using Xunit;

namespace SmartAgent.Tests;

public class GitHubSourceProviderTests
{
    [Theory]
    [InlineData("https://github.com/dotnet/aspnetcore", "dotnet", "aspnetcore", null)]
    [InlineData("https://github.com/owner/repo/tree/dev", "owner", "repo", "dev")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo", null)]
    [InlineData("owner/repo", "owner", "repo", null)]
    [InlineData("github.com/o/r/tree/feature/x", "o", "r", "feature/x")]
    public void Parse_accepts_common_github_url_shapes(string raw, string owner, string repo, string? branch)
    {
        var (o, r, b) = GitHubSourceProvider.Parse(raw);
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
        Assert.Equal(branch, b);
    }

    [Fact]
    public void Parse_rejects_non_github_urls()
    {
        Assert.Throws<ArgumentException>(() => GitHubSourceProvider.Parse("https://example.com"));
    }
}
