namespace Zapretiks.Core;

public enum ZapretRuntimeState
{
    Disconnected,
    Unavailable,
    Unknown,
    NotRunning,
    Running
}

/// <summary>A read-only process snapshot for the connected installation.</summary>
public sealed record ZapretRuntimeStatus(
    ZapretRuntimeState State,
    int MatchingProcessCount = 0,
    int OtherProcessCount = 0,
    int UnresolvedProcessCount = 0,
    int AccessDeniedProcessCount = 0,
    DateTimeOffset? CheckedAt = null)
{
    public bool EnumerationAccessDenied { get; init; }
}

public interface IZapretRuntimeInspector
{
    Task<ZapretRuntimeStatus> InspectAsync(
        ZapretInstallation? installation,
        CancellationToken cancellationToken = default);
}
