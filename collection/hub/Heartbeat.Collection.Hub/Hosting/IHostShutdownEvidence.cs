using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Collection.Hub.Hosting;

/// <summary>
/// A hosted subtree's local custody after it has stopped producing. Before that point it reports
/// Unknown. External hosts retain their own outboxes; revoking their lease does not transfer them.
/// </summary>
public interface IHostShutdownEvidence
{
    DeliveryRemainder ShutdownRemainder { get; }
    /// <summary>Optional owner diagnostics; never substitutes for custody evidence.</summary>
    string? ShutdownDetails => null;
}
