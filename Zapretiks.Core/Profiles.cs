namespace Zapretiks.Core;

public enum ProfileCategory
{
    Games,
    Services
}

public sealed record SolutionProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Author { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }
    public required ProfileCategory Category { get; init; }
    public required DateOnly UpdatedAt { get; init; }
    public bool IsVerified { get; init; }
    public bool IsDemo { get; init; }
    public string[] IncludeDomains { get; init; } = [];
    public string[] ExcludeDomains { get; init; } = [];
    public string[] IncludeNetworks { get; init; } = [];
    public string[] ExcludeNetworks { get; init; } = [];
    public string[] CompatibleStrategies { get; init; } = [];
    public string CompatibilityNotes { get; init; } = "";
}

public sealed record InstalledProfile(SolutionProfile Profile, bool IsEnabled);

public sealed record UserSettings(
    string? ZapretDirectory = null,
    string? StrategyFile = null,
    bool OnboardingCompleted = false,
    bool RememberPage = false,
    string? LastPage = null,
    bool AutoScanSteam = false);

public sealed record UserState(UserSettings Settings, InstalledProfile[] Profiles)
{
    public static UserState Empty { get; } = new(new(), []);
}

public interface ICatalogService
{
    Task<IReadOnlyList<SolutionProfile>> LoadAsync(CancellationToken cancellationToken = default);
}

public interface IUserStore
{
    string DataDirectory { get; }
    Task<UserState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserState state, CancellationToken cancellationToken = default);
}

public interface IProfileTransfer
{
    Task<SolutionProfile> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, SolutionProfile profile, CancellationToken cancellationToken = default);
}
