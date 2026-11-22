using System.Text;
using Zapretiks.Core;
using Zapretiks.Infrastructure;

internal static class ConfigurationEditorTests
{
    private const string Strategy = "general.bat";
    private const string Hosts = "list-general-user.txt";
    private const string Excluded = "list-exclude-user.txt";

    public static async Task ApplyAndUndo()
    {
        using var temp = Directory.CreateTempSubdirectory("zapretiks-apply-").ToCleanup();
        var (installation, editor) = Setup(temp.Path);
        var hosts = Path.Combine(temp.Path, "lists", Hosts);
        var excluded = Path.Combine(temp.Path, "lists", Excluded);
        // Arbitrary bytes in a manual comment, mixed newlines and no final newline.
        byte[] manual = [0xef, 0xbb, 0xbf, (byte)'#', 0xfa, .. Encoding.ASCII.GetBytes("\r\nmanual.test")];
        File.WriteAllBytes(hosts, manual);
        var first = Solution("one", ["shared.test", "manual.test", "one.test"], ["skip.test"]);
        var second = Solution("two", ["shared.test"]);
        var selected = new[] { first, second };
        var initial = await editor.CreateAsync(installation, Strategy, selected);
        Check(initial.CanApply && !initial.CanUndo && !File.Exists(excluded), "Preview must be read-only.");
        Check(initial.Files.Single(f => f.FileName == Hosts).Additions.SequenceEqual(["one.test", "shared.test"]), "Manual duplicates.");
        await editor.ApplyAsync(installation, Strategy, selected, initial);
        var installedBytes = File.ReadAllBytes(hosts);
        Check(installedBytes.AsSpan().StartsWith(manual.AsSpan(0, 3)), "BOM must remain first.");
        editor = new(Path.Combine(temp.Path, "data"));
        var current = await editor.CreateAsync(installation, Strategy, selected);
        Check(!current.CanApply && current.CanUndo, "Installed solutions must be idempotent after restart.");
        await editor.UndoAsync(installation, current);
        Check(File.ReadAllBytes(hosts).SequenceEqual(manual) && !File.Exists(excluded), "Undo restores original bytes and absence.");

        await editor.ApplyAsync(installation, Strategy, selected, await editor.CreateAsync(installation, Strategy, selected));
        const string extra = "\r\nhand-edited.test";
        File.AppendAllText(hosts, extra);
        var smaller = await editor.CreateAsync(installation, Strategy, [second]);
        Check(!smaller.CanUndo && smaller.Files.Single(f => f.FileName == Hosts).Removals.SequenceEqual(["one.test"]),
            "A shared entry is retained and an external edit disables snapshot undo.");
        await editor.ApplyAsync(installation, Strategy, [second], smaller);
        Check(!File.Exists(excluded), "Uninstalling the sole owner restores a missing list.");
        var empty = await editor.CreateAsync(installation, Strategy, []);
        Check(empty.CanApply && empty.Files.Single(f => f.FileName == Hosts).Removals.SequenceEqual(["shared.test"]), "Final removal.");
        await editor.ApplyAsync(installation, Strategy, [], empty);
        Check(File.ReadAllBytes(hosts).SequenceEqual(manual.Concat(Encoding.ASCII.GetBytes(extra))),
            "Removing every solution preserves manual bytes and later manual edits.");
    }

    public static async Task ConflictsAndRecovery()
    {
        using var temp = Directory.CreateTempSubdirectory("zapretiks-recovery-").ToCleanup();
        var (installation, editor) = Setup(temp.Path);
        var hosts = Path.Combine(temp.Path, "lists", Hosts);
        var excluded = Path.Combine(temp.Path, "lists", Excluded);
        var profiles = new[] { Solution("one", ["one.test"], ["skip.test"]) };
        var old = await editor.CreateAsync(installation, Strategy, profiles);
        File.WriteAllText(hosts, "# new comment\n");
        await ExpectStale(() => editor.ApplyAsync(installation, Strategy, profiles, old));
        Check(!File.Exists(excluded), "A stale preview must not write any target.");
        var changed = await editor.CreateAsync(installation, Strategy, profiles);
        File.AppendAllText(Path.Combine(temp.Path, Strategy), "\nrem edited");
        await ExpectStale(() => editor.ApplyAsync(installation, Strategy, profiles, changed));
        var current = await editor.CreateAsync(installation, Strategy, profiles);
        var invalid = await editor.CreateAsync(installation, "../general.bat", profiles);
        Check(!invalid.CanApply, "Unapproved strategies cannot write.");
        await editor.ApplyAsync(installation, Strategy, profiles, current);

        // Simulate power loss after the first target was written but before commit.
        var last = Directory.EnumerateFiles(Path.Combine(temp.Path, "data"), "last.json", SearchOption.AllDirectories).Single();
        File.Move(last, Path.Combine(Path.GetDirectoryName(last)!, "pending.json"));
        File.Delete(excluded);
        var recovery = await editor.CreateAsync(installation, Strategy, []);
        Check(recovery.CanUndo && !recovery.CanApply, "An interrupted operation must offer recovery.");
        await editor.UndoAsync(installation, recovery);
        Check(File.ReadAllText(hosts) == "# new comment\n" && !File.Exists(excluded), "Recovery respects each file boundary.");

        await editor.ApplyAsync(installation, Strategy, profiles, await editor.CreateAsync(installation, Strategy, profiles));
        var beforeManualChange = await editor.CreateAsync(installation, Strategy, []);
        var edited = File.ReadAllText(hosts).Replace("one.test", "manual.test", StringComparison.Ordinal);
        File.WriteAllText(hosts, edited);
        await ExpectStale(() => editor.UndoAsync(installation, beforeManualChange));
        var damaged = await editor.CreateAsync(installation, Strategy, []);
        Check(!damaged.CanApply && !damaged.CanUndo && File.ReadAllText(hosts) == edited, "Edited ownership blocks are preserved.");
    }

    private static (ZapretInstallation, FlowsealConfigurationEditor) Setup(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        File.WriteAllText(Path.Combine(root, Strategy), $"winws --hostlist=%LISTS%{Hosts} --hostlist-exclude=%LISTS%{Excluded}");
        return (new(root, true, "test", [new(Strategy, "General")], []), new(Path.Combine(root, "data")));
    }

    private static InstalledProfile Solution(string id, string[] includes, string[]? excludes = null) =>
        new(new() { Id = id, Name = id, Author = "test", Description = "test", Source = "test", Version = "1",
            UpdatedAt = new(2026, 1, 1), Category = ProfileCategory.Services, IncludeDomains = includes,
            ExcludeDomains = excludes ?? [] }, true);

    private static async Task ExpectStale(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected stale operation to be refused.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
