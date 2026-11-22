using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class FlowsealRuntimeController : IZapretRuntimeController
{
    private readonly IZapretRuntimeInspector inspector;
    private readonly bool requireElevation;
    private readonly string commandLock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private WindowsStrategyJob? job;
    private ManagedZapretRun? managed;

    public FlowsealRuntimeController() : this(new FlowsealRuntimeInspector(), true, @"Local\Zapretiks.Flowseal.Control") { }

    internal FlowsealRuntimeController(IZapretRuntimeInspector inspector, bool requireElevation, string commandLock)
    {
        this.inspector = inspector;
        this.requireElevation = requireElevation;
        this.commandLock = commandLock;
    }

    public ManagedZapretRun? Current
    {
        get
        {
            lock (sync)
                return job?.EngineIsAlive == true ? managed : null;
        }
    }

    public Task<RuntimeControlResult> StartAsync(ZapretInstallation installation, string strategyFile,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => ChangeStrategy(installation, strategyFile, false, token), cancellationToken);

    public Task<RuntimeControlResult> SwitchAsync(ZapretInstallation installation, string strategyFile,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => ChangeStrategy(installation, strategyFile, true, token), cancellationToken);

    public Task<RuntimeControlResult> StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(_ => StopCurrent(), cancellationToken);

    private Task<RuntimeControlResult> ExecuteAsync(Func<CancellationToken, RuntimeControlResult> action,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows())
            return new RuntimeControlResult(false, "Управление zapret доступно в Windows.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            // All app instances in the desktop session cooperate. Stop/new start during a switch cannot interleave.
            using var marker = new EventWaitHandle(false, EventResetMode.ManualReset, commandLock, out bool acquired);
            if (!acquired)
                return new(false, "Управление zapret уже выполняется. Дождитесь завершения операции.");
            return action(token);
        }
        catch (Exception error) when (Expected(error))
        {
            return new(false, Explain(error));
        }
    }, cancellationToken);

    private RuntimeControlResult ChangeStrategy(ZapretInstallation installation, string strategy, bool switching,
        CancellationToken token)
    {
        if (!installation.IsValid || !ProfileValidation.IsStrategyFileName(strategy) ||
            !installation.Strategies.Any(item => Same(item.FileName, strategy)))
            return new(false, "Выберите доступную стратегию из подключённой папки Flowseal.");
        var root = Path.GetFullPath(installation.Directory);
        if (!Path.IsPathFullyQualified(installation.Directory) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            root.IndexOfAny(['&', '%', '!', '^', '\r', '\n']) >= 0)
            return new(false, "Переместите сборку в локальную папку с обычным именем, например C:\\Zapret.");
        var fresh = new FlowsealInspector().InspectAsync(root, token).GetAwaiter().GetResult();
        if (!fresh.IsValid || !fresh.Strategies.Any(item => Same(item.FileName, strategy)))
            return new(false, "Файлы сборки изменились. Проверьте папку Flowseal и выберите стратегию ещё раз.");
        if (requireElevation && OperatingSystem.IsWindows() && !IsAdministrator())
            return new(false, "Для запуска zapret нужны права администратора. Закройте Zapretiks и откройте его от имени администратора.");

        var previous = Current;
        if (switching && (previous is null || !Same(previous.Directory, root)))
            return new(false, "Переключать можно только стратегию, которую Zapretiks включил в этой папке.");
        if (!switching && previous is not null)
            return new(false, "Zapretiks уже управляет запущенной стратегией.");

        var conflict = RuntimeConflict(fresh, previous is null ? 0 : 1, token);
        if (conflict is not null)
            return new(false, conflict);
        if (previous is not null && Same(previous.StrategyFile, strategy))
            return new(true, "Эта стратегия уже работает.");

        token.ThrowIfCancellationRequested();
        if (previous is not null)
            StopCurrent();
        try
        {
            StartConfirmed(fresh, strategy, token);
            return new(true, $"Стратегия {strategy} запущена.");
        }
        catch (Exception error) when (previous is not null && Expected(error))
        {
            var reason = Explain(error);
            try
            {
                StartConfirmed(fresh, previous.StrategyFile, token);
                return new(false, $"{reason} Снова работает прежняя стратегия {previous.StrategyFile}.");
            }
            catch (Exception recoveryError) when (Expected(recoveryError))
            {
                return new(false, $"{reason} Прежнюю стратегию также не удалось запустить. {Explain(recoveryError)}");
            }
        }
    }

    private void StartConfirmed(ZapretInstallation installation, string strategy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var conflict = RuntimeConflict(installation, 0, token);
        if (conflict is not null)
            throw new InvalidOperationException(conflict);
        if (!File.Exists(Path.Combine(installation.Directory, strategy)))
            throw new InvalidOperationException("Файл выбранной стратегии исчез из папки сборки.");
        WindowsStrategyJob? candidate = WindowsStrategyJob.Launch(installation.Directory, strategy);
        try
        {
            var expected = Path.Combine(installation.Directory, "bin", "winws.exe");
            var elapsed = Stopwatch.StartNew();
            while (!candidate.FindEngine(expected))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(12))
                    throw new InvalidOperationException("За 12 секунд стратегия не запустила zapret. Проверьте сборку и выбранный BAT-файл.");
                if (token.WaitHandle.WaitOne(100))
                    token.ThrowIfCancellationRequested();
            }

            // Many bad arguments or driver errors end winws immediately after process creation.
            if (token.WaitHandle.WaitOne(600))
                token.ThrowIfCancellationRequested();
            if (!candidate.EngineIsAlive)
                throw new InvalidOperationException("zapret завершился сразу после запуска. Проверьте сборку и выбранную стратегию.");
            conflict = RuntimeConflict(installation, 1, token);
            if (conflict is not null)
                throw new InvalidOperationException(conflict);
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                job?.Dispose();
                job = candidate;
                managed = new(installation.Directory, strategy);
                candidate = null;
            }
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private string? RuntimeConflict(ZapretInstallation installation, int expected, CancellationToken token)
    {
        var state = inspector.InspectAsync(installation, token).GetAwaiter().GetResult();
        if (state.OtherProcessCount > 0)
            return "Уже работает zapret из другой папки. Остановите его перед запуском этой сборки.";
        if (state.MatchingProcessCount > expected)
            return "В этой папке уже запущен zapret. Остановите его там, где он был включён, и повторите попытку.";
        if (state.UnresolvedProcessCount > 0 || state.MatchingProcessCount != expected ||
            state.State != (expected == 0 ? ZapretRuntimeState.NotRunning : ZapretRuntimeState.Running))
            return "Не удалось подтвердить состояние zapret. Обновите проверку и повторите попытку.";
        return null;
    }

    private RuntimeControlResult StopCurrent()
    {
        lock (sync)
        {
            if (job is null)
                return new(false, "Zapretiks сейчас не управляет запущенной стратегией.");
            job.Stop();
            job.Dispose();
            job = null;
            managed = null;
        }
        return new(true, "zapret выключен.");
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (sync)
        {
            job?.Dispose();
            job = null;
            managed = null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool Same(string first, string second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private static bool Expected(Exception error) =>
        error is Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException;

    private static string Explain(Exception error) => error switch
    {
        Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException =>
            "Windows отказала в доступе. Проверьте права администратора и доступ к выбранной папке.",
        Win32Exception windows => $"Windows не смогла выполнить команду (код {windows.NativeErrorCode}).",
        InvalidOperationException => error.Message,
        _ => "Не удалось прочитать файлы стратегии. Проверьте выбранную папку Flowseal."
    };
}
