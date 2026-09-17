using SmartAgent.Domain;

namespace SmartAgent.Web.Models;

public class RunInput
{
    /// <summary>a / b / c select.</summary>
    public SourceType SourceType { get; set; } = SourceType.ZipArchive;

    public IFormFile? ZipFile { get; set; }

    public string? GitHubUrl { get; set; }
    public string? WebsiteUrl { get; set; }

    /// <summary>1..8, keeps the run's prompt scope tight.</summary>
    public int ScopeLimit { get; set; } = 3;
    public FocusArea Focus { get; set; } = FocusArea.Balanced;
    public string? Notes { get; set; }
}
