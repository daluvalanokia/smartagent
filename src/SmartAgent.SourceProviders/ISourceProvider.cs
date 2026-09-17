using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>Retrieves app source for one input mode (zip / GitHub / website).</summary>
public interface ISourceProvider
{
    SourceType SourceType { get; }
    Task<SourceSnapshot> FetchAsync(SourceRequest request);
}
