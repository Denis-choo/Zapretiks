using Zapretiks.Core;
using Zapretiks.Infrastructure;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Canonical domain and network validation", ValidateEntries),
    ("Embedded catalog contains explicitly marked demo profiles", LoadCatalog),
    ("Local state survives a fresh store instance", PersistState),
    ("Duplicate IDs leave the last saved state readable", RejectDuplicates),
    ("Profile exchange validates the schema and clears verification", TransferProfile),
    ("Flowseal folder validation and strategy discovery", InspectInstallation),
    ("Preview skips duplicates, conflicts, and demo entries without writing", PreviewChanges),
    ("Disabled IPSet keeps its mode marker", PreserveIpsetMode),
    ("Steam scanner reads an additional library and ignores partial installs", ScanSteamLibraries)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{error}");
        failures++;
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task ValidateEntries()
{
    var normalized = ProfileValidation.Normalize(Sample() with
    {
        IncludeDomains = ["PLAY.EXAMPLE.TEST.", "play.example.test"],
        IncludeNetworks = ["192.0.2.40/24", "192.0.2.0/24", "2001:db8::40/64"]
    });
    Check(normalized.IncludeDomains.SequenceEqual(["play.example.test"]), "Domains should be canonical and unique.");
    Check(normalized.IncludeNetworks.SequenceEqual(["192.0.2.0/24", "2001:db8::/64"]), "CIDRs should be canonical and unique.");
    Check(ProfileValidation.NormalizeDomain("https://play.example.test/") is null, "URLs must be rejected.");
    Check(ProfileValidation.NormalizeNetwork("127.1") is null, "Noncanonical IPv4 forms must be rejected.");
    Check(ProfileValidation.NormalizeNetwork("192.0.2.0/33") is null, "Invalid prefixes must be rejected.");
    Check(!ProfileValidation.IsStrategyFileName("../general.bat"), "Strategy paths must be rejected.");
    return Task.CompletedTask;
}

static async Task LoadCatalog()
{
    var profiles = await new EmbeddedCatalogService().LoadAsync();
    Check(profiles.Count >= 4 && profiles.All(item => item.IsDemo && !item.IsVerified), "Seed entries must carry demo provenance.");
    Check(profiles.Select(item => item.Category).Distinct().Count() == 2, "Both demo categories should be represented.");
}

static async Task PersistState()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-state-").ToCleanup();
    var store = new JsonUserStore(directory.Path);
    Check((await store.LoadAsync()).Profiles.Length == 0, "New libraries start empty.");
    await store.SaveAsync(new(new UserSettings(directory.Path, "general (ALT).bat", true, AutoScanSteam: true), [new(Sample(), true)]));
    var saved = await new JsonUserStore(directory.Path).LoadAsync();
    Check(saved.Settings.AutoScanSteam && saved.Settings.OnboardingCompleted && saved.Settings.StrategyFile == "general (ALT).bat",
        "Preferences must persist.");
    Check(saved.Profiles.Single().IsEnabled && saved.Profiles.Single().Profile.Id == Sample().Id, "Installed state must persist.");
}

static async Task RejectDuplicates()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-dupes-").ToCleanup();
    var store = new JsonUserStore(directory.Path);
    var state = new UserState(new(), [new(Sample(), false)]);
    await store.SaveAsync(state);
    await RejectAsync(() => store.SaveAsync(state with
    {
        Profiles = [.. state.Profiles, new(Sample() with { Id = Sample().Id.ToUpperInvariant() }, true)]
    }));
    Check((await store.LoadAsync()).Profiles.Length == 1, "Failed saves must preserve the previous state.");
}

static async Task TransferProfile()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-profile-").ToCleanup();
    var path = Path.Combine(directory.Path, "profile.json");
    var transfer = new JsonProfileTransfer();
    await transfer.ExportAsync(path, Sample() with { IsVerified = true });
    var imported = await transfer.ImportAsync(path);
    Check(!imported.IsVerified && imported.Source == "Локальный импорт", "Imported profiles cannot claim local verification.");
    var valid = await File.ReadAllTextAsync(path);
    await File.WriteAllTextAsync(path, valid.Insert(1, "\"command\":\"example.exe\","));
    await RejectAsync(() => transfer.ImportAsync(path));
    await File.WriteAllTextAsync(path, valid.Insert(1, "\"id\":\"duplicate\","));
    await RejectAsync(() => transfer.ImportAsync(path));
    await File.WriteAllBytesAsync(path, new byte[JsonProfileTransfer.MaxProfileBytes + 1]);
    await RejectAsync(() => transfer.ImportAsync(path));
}

static async Task InspectInstallation()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-inspect-").ToCleanup();
    var inspector = new FlowsealInspector();
    Check(!(await inspector.InspectAsync(directory.Path)).IsValid, "An empty folder cannot be a valid installation.");
    WriteFixture(directory.Path);
    var installation = await inspector.InspectAsync(directory.Path);
    Check(installation.IsValid && installation.Version == "1.2.3" && installation.HasServiceScript, "Expected fixture version and components.");
    Check(installation.Strategies.Any(item => item.FileName == "general (ALT).bat"), "ALT strategies should be discoverable.");
    Check(installation.Strategies.All(item => item.FileName != "other.bat"), "Unrelated batch files must be ignored.");
}

static async Task PreviewChanges()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-preview-").ToCleanup();
    WriteFixture(directory.Path);
    var list = Path.Combine(directory.Path, "lists", "list-general-user.txt");
    const string original = "# manual\nmanual.example.test\n";
    await File.WriteAllTextAsync(list, original);
    var installation = await new FlowsealInspector().InspectAsync(directory.Path);
    var include = Sample() with { IncludeDomains = ["manual.example.test", "new.example.test", "conflict.example.test"] };
    var exclude = Sample() with { Id = "exclude", ExcludeDomains = ["conflict.example.test"] };
    var demo = Sample() with { Id = "demo", IsDemo = true, IncludeDomains = ["ignored.example.test"] };
    var plan = await new FlowsealConfigurationPreview().CreateAsync(installation, "general.bat",
        [new(include, true), new(exclude, true), new(demo, true), new(Sample(), false)]);
    var additions = plan.Files.Single(item => item.FileName.EndsWith("list-general-user.txt", StringComparison.Ordinal)).Additions;
    Check(additions.SequenceEqual(["new.example.test"]), "The plan must suppress manual, demo, and conflicted includes.");
    Check(plan.Warnings.Count > 0, "The plan must explain conflicts or demo entries.");
    Check(await File.ReadAllTextAsync(list) == original, "Preview must preserve manual data byte for byte.");
}

static async Task PreserveIpsetMode()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-ipset-").ToCleanup();
    WriteFixture(directory.Path);
    var ipset = Path.Combine(directory.Path, "lists", "ipset-all.txt");
    const string marker = "203.0.113.113/32\n";
    await File.WriteAllTextAsync(ipset, marker);
    var installation = await new FlowsealInspector().InspectAsync(directory.Path);
    var plan = await new FlowsealConfigurationPreview().CreateAsync(installation, "general.bat",
        [new(Sample() with { IncludeNetworks = ["192.0.2.0/24"] }, true)]);
    Check(plan.Files.Single(item => item.FileName.EndsWith("ipset-all.txt", StringComparison.Ordinal)).Additions.Count == 0,
        "Adding IP entries must not flip the IPSet mode.");
    Check(await File.ReadAllTextAsync(ipset) == marker, "The IPSet marker must be preserved.");
}

static async Task ScanSteamLibraries()
{
    using var directory = Directory.CreateTempSubdirectory("zapretiks-steam-").ToCleanup();
    var root = Path.Combine(directory.Path, "Steam");
    var secondary = Path.Combine(directory.Path, "Extra library");
    var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
    var extraApps = Directory.CreateDirectory(Path.Combine(secondary, "steamapps")).FullName;
    var escaped = secondary.Replace("\\", "\\\\", StringComparison.Ordinal);
    await File.WriteAllTextAsync(Path.Combine(apps, "libraryfolders.vdf"),
        $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{escaped}\" }} }}");
    Directory.CreateDirectory(Path.Combine(extraApps, "common", "Example"));
    await File.WriteAllTextAsync(Path.Combine(extraApps, "appmanifest_42.acf"),
        "\"AppState\" { \"appid\" \"42\" \"name\" \"Example\" \"installdir\" \"Example\" \"StateFlags\" \"4\" }");
    await File.WriteAllTextAsync(Path.Combine(extraApps, "appmanifest_43.acf"),
        "\"AppState\" { \"appid\" \"43\" \"name\" \"Partial\" \"installdir\" \"Example\" \"StateFlags\" \"2\" }");
    var result = await new SteamScanner(root).ScanAsync();
    Check(result.SteamFound && result.Games.Single().AppId == 42, "Only the fully installed game should be detected.");
}

static void WriteFixture(string directory)
{
    var bin = Directory.CreateDirectory(Path.Combine(directory, "bin")).FullName;
    Directory.CreateDirectory(Path.Combine(directory, "lists"));
    foreach (var component in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
        File.WriteAllText(Path.Combine(bin, component), "test fixture, non-executable");
    const string strategy = "--hostlist=\"%LISTS%list-general-user.txt\" --hostlist-exclude=\"%LISTS%list-exclude-user.txt\" "
        + "--ipset=\"%LISTS%ipset-all.txt\" --ipset-exclude=\"%LISTS%ipset-exclude-user.txt\"";
    File.WriteAllText(Path.Combine(directory, "general.bat"), strategy);
    File.WriteAllText(Path.Combine(directory, "general (ALT).bat"), strategy);
    File.WriteAllText(Path.Combine(directory, "other.bat"), "fixture");
    File.WriteAllText(Path.Combine(directory, "service.bat"), "set \"LOCAL_VERSION=1.2.3\"\n");
}

static SolutionProfile Sample() => new()
{
    Id = "example",
    Name = "Example",
    Description = "Example profile for testing",
    Author = "Zapretiks",
    Version = "1.0",
    Source = "Tests",
    Category = ProfileCategory.Games,
    UpdatedAt = new DateOnly(2026, 11, 12)
};

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task RejectAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException("Expected invalid input to be rejected.");
}

file static class TestDirectories
{
    public static Cleanup ToCleanup(this DirectoryInfo directory) => new(directory.FullName);

    internal sealed record Cleanup(string Path) : IDisposable
    {
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
