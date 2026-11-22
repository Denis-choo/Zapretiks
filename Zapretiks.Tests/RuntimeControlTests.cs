using System.Diagnostics;
using System.Text;
using Zapretiks.Core;
using Zapretiks.Infrastructure;

internal static class RuntimeControlTests
{
    public static async Task Lifecycle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        using var control = fixture.NewController();
        var installation = fixture.Installation;
        var started = await control.StartAsync(installation, "general.bat");
        Check(started.Succeeded && control.Current?.StrategyFile == "general.bat", started.Message);
        var rejected = await control.StartAsync(installation, "general (ALT).bat");
        Check(!rejected.Succeeded && control.Current?.StrategyFile == "general.bat", rejected.Message);
        var switched = await control.SwitchAsync(installation, "general (ALT).bat");
        Check(switched.Succeeded && control.Current?.StrategyFile == "general (ALT).bat", switched.Message);

        var originalRun = control.Current!;
        var restarted = await control.RestartAsync(installation, originalRun);
        var restartedRun = control.Current!;
        Check(restarted.Succeeded && restartedRun?.StrategyFile == originalRun.StrategyFile &&
            !ReferenceEquals(originalRun, restartedRun), restarted.Message);
        var staleRestart = await control.RestartAsync(installation, originalRun);
        Check(!staleRestart.Succeeded && ReferenceEquals(restartedRun, control.Current),
            "A restart must apply to the exact run captured by the caller.");

        // An external engine at exactly the same path is still outside the controller's job.
        using var external = Process.Start(new ProcessStartInfo(fixture.Engine, "--runtime-test-engine")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            var blockedRestart = await control.RestartAsync(installation, restartedRun!);
            Check(!blockedRestart.Succeeded && ReferenceEquals(restartedRun, control.Current) && !external.HasExited,
                "A restart must leave both processes alive when the installation has an external engine.");
            var stopped = await control.StopAsync();
            Check(stopped.Succeeded && control.Current is null && !external.HasExited, stopped.Message);
            var externalConflict = await control.StartAsync(installation, "general.bat");
            Check(!externalConflict.Succeeded && control.Current is null && !external.HasExited,
                "The external process must block start and survive it.");
        }
        finally
        {
            if (!external.HasExited) external.Kill();
            await external.WaitForExitAsync();
        }
        Check((await control.StartAsync(installation, "general.bat")).Succeeded, "Start after stop must succeed.");
        control.Dispose();
        await fixture.AssertNoEngine();
    }

    public static async Task FailedSwitch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        using var control = fixture.NewController();
        var installation = fixture.Installation;
        var started = await control.StartAsync(installation, "general.bat");
        Check(started.Succeeded, started.Message);
        var invalid = await control.SwitchAsync(installation, @"..\general.bat");
        Check(!invalid.Succeeded && control.Current?.StrategyFile == "general.bat",
            "An invalid candidate must not interrupt a running strategy.");
        var failure = await control.SwitchAsync(installation, "general (broken).bat");
        Check(!failure.Succeeded && control.Current?.StrategyFile == "general.bat", failure.Message);
        var stopped = await control.StopAsync();
        Check(stopped.Succeeded, stopped.Message);
        await fixture.AssertNoEngine();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture(ZapretInstallation installation) : IDisposable
    {
        public ZapretInstallation Installation => installation;
        public string Engine => Path.Combine(installation.Directory, "bin", "winws.exe");
        private readonly FixtureInspector inspector = new();
        private readonly string commandLock = @"Local\Zapretiks.Fixture." + Guid.NewGuid().ToString("N");
        public FlowsealRuntimeController NewController() => new(inspector, false, commandLock);

        public static async Task<Fixture> Create()
        {
            var root = Directory.CreateTempSubdirectory("Zapretiks job (test) ").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "lists"));
            foreach (var name in new[] { "Zapretiks.Tests.dll", "Zapretiks.Tests.runtimeconfig.json", "Zapretiks.Tests.deps.json",
                "Zapretiks.Core.dll", "Zapretiks.Infrastructure.dll", "Zapretiks.Tests.exe" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(bin, name.EndsWith(".exe") ? "winws.exe" : name));
            foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
                File.WriteAllText(Path.Combine(bin, name), "");
            foreach (var name in new[] { "general.bat", "general (ALT).bat" })
                File.WriteAllText(Path.Combine(root, name), "@start \"fixture\" /b \"%~dp0bin\\winws.exe\" --runtime-test-engine\r\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(root, "general (broken).bat"), "@exit /b 1\r\n", Encoding.ASCII);
            return new(await new FlowsealInspector().InspectAsync(root));
        }

        public async Task AssertNoEngine()
        {
            var until = Stopwatch.StartNew();
            while ((await inspector.InspectAsync(installation)).MatchingProcessCount != 0)
            {
                Check(until.Elapsed.TotalSeconds < 5, "The fixture engine must exit when the job closes.");
                await Task.Delay(50);
            }
        }

        public void Dispose()
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    Directory.Delete(installation.Directory, true);
                    return;
                }
                catch (Exception error) when (elapsed.Elapsed.TotalSeconds < 5 &&
                    error is UnauthorizedAccessException or IOException)
                {
                    // Windows may briefly keep the executable mapped after job termination.
                    Thread.Sleep(100);
                }
            }
        }
    }

    // Any real Flowseal installation on the test machine is unrelated to the temporary fixture.
    private sealed class FixtureInspector : IZapretRuntimeInspector
    {
        private readonly FlowsealRuntimeInspector actual = new();
        public async Task<ZapretRuntimeStatus> InspectAsync(ZapretInstallation? installation, CancellationToken cancellationToken = default)
        {
            var snapshot = await actual.InspectAsync(installation, cancellationToken);
            return new(snapshot.MatchingProcessCount > 0 ? ZapretRuntimeState.Running : ZapretRuntimeState.NotRunning,
                MatchingProcessCount: snapshot.MatchingProcessCount);
        }
    }
}
