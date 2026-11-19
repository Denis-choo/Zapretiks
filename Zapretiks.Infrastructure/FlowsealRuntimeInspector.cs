using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class FlowsealRuntimeInspector : IZapretRuntimeInspector
{
    private readonly IRuntimeProcessSource processes;

    public FlowsealRuntimeInspector() : this(new WindowsRuntimeProcessSource()) { }

    internal FlowsealRuntimeInspector(IRuntimeProcessSource processes) => this.processes = processes;

    public Task<ZapretRuntimeStatus> InspectAsync(ZapretInstallation? installation,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(installation, cancellationToken), cancellationToken);

    private ZapretRuntimeStatus Inspect(ZapretInstallation? installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (installation is null)
            return new(ZapretRuntimeState.Disconnected);

        if (!installation.IsValid)
            return new(ZapretRuntimeState.Unavailable);

        var expectedPath = NormalizePath(Path.Combine(installation.Directory, "bin", "winws.exe"));
        if (expectedPath is null)
            return new(ZapretRuntimeState.Unavailable);

        RuntimeProcessSnapshot snapshot;
        try
        {
            snapshot = processes.Capture(cancellationToken);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // OS errors can contain process details. Only aggregate state leaves this service.
            return new(ZapretRuntimeState.Unknown, CheckedAt: DateTimeOffset.Now)
            {
                EnumerationAccessDenied = error is UnauthorizedAccessException ||
                    error is Win32Exception { NativeErrorCode: 5 }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var matching = 0;
        var other = 0;
        var unresolved = 0;
        var denied = 0;
        foreach (var process in snapshot.Processes)
        {
            var path = NormalizePath(process.ExecutablePath);
            if (path is null)
            {
                unresolved++;
                if (process.AccessDenied)
                    denied++;
            }
            else if (StringComparer.OrdinalIgnoreCase.Equals(path, expectedPath))
                matching++;
            else
                other++;
        }

        var state = matching > 0 ? ZapretRuntimeState.Running
            : !snapshot.EnumerationSucceeded || unresolved > 0 ? ZapretRuntimeState.Unknown
            : ZapretRuntimeState.NotRunning;
        return new(state, matching, other, unresolved, denied, DateTimeOffset.Now);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

internal sealed record RuntimeProcessObservation(string? ExecutablePath, bool AccessDenied = false);
internal sealed record RuntimeProcessSnapshot(bool EnumerationSucceeded, IReadOnlyList<RuntimeProcessObservation> Processes);

internal interface IRuntimeProcessSource
{
    RuntimeProcessSnapshot Capture(CancellationToken cancellationToken);
}

internal sealed class WindowsRuntimeProcessSource : IRuntimeProcessSource
{
    public RuntimeProcessSnapshot Capture(CancellationToken cancellationToken) => OperatingSystem.IsWindows()
        ? CaptureWindows(cancellationToken) : new(false, []);

    [SupportedOSPlatform("windows")]
    private static RuntimeProcessSnapshot CaptureWindows(CancellationToken cancellationToken)
    {
        var candidates = Process.GetProcessesByName("winws");
        var observations = new List<RuntimeProcessObservation>(candidates.Length);
        try
        {
            foreach (var process in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var observation = ReadProcess(process.Id);
                    if (observation is not null)
                        observations.Add(observation);
                }
                catch (InvalidOperationException)
                {
                    // The candidate may exit between enumeration and opening its handle.
                }
            }
        }
        finally
        {
            foreach (var process in candidates)
                process.Dispose();
        }

        return new(true, observations);
    }

    [SupportedOSPlatform("windows")]
    private static RuntimeProcessObservation? ReadProcess(int processId)
    {
        const uint limitedQueryInformation = 0x1000;
        const int invalidParameter = 87;
        const int accessDenied = 5;
        using var handle = OpenProcess(limitedQueryInformation, false, processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error == invalidParameter ? null : new(null, error == accessDenied);
        }

        var path = new StringBuilder(32768);
        var size = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            return HasExited(handle) ? null : new(null, error == accessDenied);
        }

        if (!GetExitCodeProcess(handle, out var exitCode))
            return new(null, Marshal.GetLastWin32Error() == accessDenied);
        if (exitCode != 259)
            return null;

        // A PID may have been reused since GetProcessesByName took its snapshot.
        var executablePath = path.ToString();
        return Path.GetFileName(executablePath).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)
            ? new(executablePath) : null;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExited(SafeProcessHandle handle) =>
        GetExitCodeProcess(handle, out var exitCode) && exitCode != 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imageName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
