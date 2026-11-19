using System.ComponentModel;
using Zapretiks.Core;
using Zapretiks.Infrastructure;

internal static class RuntimeStatusTests
{
    public static async Task ScopedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "zapretiks-runtime");
        var expected = Path.Combine(root, "bin", "winws.exe");
        var other = Path.Combine(root + "-other", "bin", "winws.exe");
        var nested = Path.Combine(root, "bin", "another", "winws.exe");
        var source = new FakeProcesses(new(true, [new(other), new(nested)]));
        var inspector = new FlowsealRuntimeInspector(source);
        var installation = Installation(root);

        var unrelated = await inspector.InspectAsync(installation);
        Check(unrelated is { State: ZapretRuntimeState.NotRunning, MatchingProcessCount: 0, OtherProcessCount: 2 },
            "A matching process name or path prefix must not confirm the connected installation.");

        source.Snapshot = new(true, [new(expected.ToUpperInvariant()), new(other), new(null, AccessDenied: true)]);
        var running = await inspector.InspectAsync(installation);
        Check(running is { State: ZapretRuntimeState.Running, MatchingProcessCount: 1, OtherProcessCount: 1,
            UnresolvedProcessCount: 1, AccessDeniedProcessCount: 1, CheckedAt: not null },
            "A readable matching path must confirm running state and retain aggregate diagnostics.");

        source.Snapshot = new(true, [new(expected), new(expected), new(other)]);
        var multiple = await inspector.InspectAsync(installation);
        Check(multiple is { State: ZapretRuntimeState.Running, MatchingProcessCount: 2, OtherProcessCount: 1 },
            "Multiple connected instances must be represented explicitly.");

        source.Snapshot = new(true, [new(expected)]);
        var firstFolder = await inspector.InspectAsync(installation);
        var changedFolder = await inspector.InspectAsync(Installation(root + "-other"));
        Check(firstFolder.State == ZapretRuntimeState.Running && changedFolder.State == ZapretRuntimeState.NotRunning,
            "A status result must be recalculated for every newly selected folder.");

        if (OperatingSystem.IsWindows())
        {
            source.Snapshot = new(true, [new(@"\\?\" + expected)]);
            Check((await inspector.InspectAsync(installation)).State == ZapretRuntimeState.Running,
                "Extended Windows path prefixes should match the connected executable.");
        }
    }

    public static async Task UnknownStates()
    {
        var installation = Installation(Path.Combine(Path.GetTempPath(), "zapretiks-runtime"));
        var source = new FakeProcesses(new(true, []));
        var inspector = new FlowsealRuntimeInspector(source);
        Check((await inspector.InspectAsync(null)).State == ZapretRuntimeState.Disconnected,
            "An absent installation should stay disconnected.");
        Check((await inspector.InspectAsync(installation with { IsValid = false })).State == ZapretRuntimeState.Unavailable,
            "An invalid installation cannot have a confirmed runtime status.");
        Check(source.CaptureCount == 0, "Disconnected or invalid installations must avoid process enumeration.");
        Check((await inspector.InspectAsync(installation)).State == ZapretRuntimeState.NotRunning,
            "Successful empty enumeration should report missing process.");

        source.Snapshot = new(true, [new(null, AccessDenied: true)]);
        Check((await inspector.InspectAsync(installation)) is { State: ZapretRuntimeState.Unknown, AccessDeniedProcessCount: 1 },
            "Unreadable executable paths cannot be presented as a missing process.");

        source.Snapshot = new(false, []);
        Check((await inspector.InspectAsync(installation)).State == ZapretRuntimeState.Unknown,
            "Failed enumeration must report unknown state.");

        source.Error = new Win32Exception(5, "Private details from the operating system");
        var inaccessible = await inspector.InspectAsync(installation);
        Check(inaccessible is { State: ZapretRuntimeState.Unknown, EnumerationAccessDenied: true } &&
            !inaccessible.ToString().Contains("Private", StringComparison.Ordinal),
            "OS exception text must not escape through the runtime status result.");
    }

    public static async Task Cancellation()
    {
        var source = new FakeProcesses(new(true, []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new FlowsealRuntimeInspector(source).InspectAsync(Installation(Path.GetTempPath()), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Check(source.CaptureCount == 0, "Cancelled checks must avoid process enumeration.");
            return;
        }

        throw new InvalidOperationException("Expected cancellation to propagate.");
    }

    private static ZapretInstallation Installation(string root) => new(root, true, null, [], []);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeProcesses(RuntimeProcessSnapshot snapshot) : IRuntimeProcessSource
    {
        public RuntimeProcessSnapshot Snapshot { get; set; } = snapshot;
        public int CaptureCount { get; private set; }
        public Exception? Error { get; set; }

        public RuntimeProcessSnapshot Capture(CancellationToken cancellationToken)
        {
            CaptureCount++;
            if (Error is not null)
                throw Error;
            return Snapshot;
        }
    }
}
