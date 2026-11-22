using System.Text;
using System.Text.Json;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class FlowsealConfigurationEditor(string dataDirectory) : IConfigurationEditor
{
    private sealed record Review(ConfigurationPlan Plan, ListEdit[] Edits);

    public Task<ConfigurationPlan> CreateAsync(ZapretInstallation? installation, string? strategyFile,
        IEnumerable<InstalledProfile> profiles, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (installation is not { IsValid: true })
                return new ConfigurationPlan([], ["Подключите папку Flowseal, чтобы проверить изменения."]);
            try { return (await ReviewAsync(installation, strategyFile, profiles, cancellationToken)).Plan; }
            catch (Exception error) when (LocalFile.IsReadError(error))
            {
                return new ConfigurationPlan([], [error is InvalidDataException ? error.Message :
                    "Не удалось прочитать списки или резервную копию. Проверьте доступ к папкам и повторите проверку."]);
            }
        }, cancellationToken);

    public Task ApplyAsync(ZapretInstallation installation, string? strategyFile, IEnumerable<InstalledProfile> profiles,
        ConfigurationPlan reviewed, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (!reviewed.CanApply || !installation.IsValid) throw Stale();
            var transaction = new ListTransaction(dataDirectory, installation.Directory);
            using var operation = transaction.Lock();
            var review = await ReviewAsync(installation, strategyFile, profiles, cancellationToken);
            if (!review.Plan.CanApply || review.Plan.Revision != reviewed.Revision) throw Stale();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(review.Edits);
        }, cancellationToken);

    public Task UndoAsync(ZapretInstallation installation, ConfigurationPlan reviewed,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!reviewed.CanUndo || !installation.IsValid) throw Stale();
        var transaction = new ListTransaction(dataDirectory, installation.Directory);
        using var operation = transaction.Lock();
        var (state, journal) = transaction.Load();
        if (state is null || journal is null) throw Stale();
        var current = journal.Files.ToDictionary(f => f.Name, f => ListTransaction.Read(transaction.ListPath(f.Name)));
        if (UndoStamp(installation, state, journal, current) != reviewed.UndoRevision) throw Stale();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Restore(state, journal);
    }, cancellationToken);

    private async Task<Review> ReviewAsync(ZapretInstallation installation, string? strategyFile,
        IEnumerable<InstalledProfile> profiles, CancellationToken token)
    {
        var transaction = new ListTransaction(dataDirectory, installation.Directory);
        if (!Directory.Exists(Path.Combine(installation.Directory, "lists")))
            throw new InvalidDataException("Папка lists недоступна. Проверьте подключённую сборку.");
        var files = new Dictionary<string, byte[]?>();
        foreach (var name in FlowsealConfigurationPreview.FileNames)
        {
            token.ThrowIfCancellationRequested();
            files[name] = ListTransaction.Read(transaction.ListPath(name));
        }
        var (state, journal) = transaction.Load();
        var canUndo = ListTransaction.CanRestore(state, journal, files);
        var undoStamp = canUndo ? UndoStamp(installation, state!, journal!, files) : null;
        var warnings = new List<string>();
        if (ListTransaction.Interrupted(state))
        {
            warnings.Add(canUndo
                ? "Запись была прервана. Нажмите «Отменить изменения», чтобы восстановить списки."
                : "Запись была прервана, а списки изменились извне. Проверьте файлы и резервную копию в папке данных.");
            return new(new([], warnings) { CanUndo = canUndo, UndoRevision = undoStamp }, []);
        }
        if (journal is not null && !canUndo)
            warnings.Add("После последней записи списки изменились извне. Автоматический откат этой записи недоступен.");

        var lists = files.ToDictionary(file => file.Key, file => ManagedList.Read(file.Key, file.Value));
        var baseline = lists.ToDictionary(list => list.Key, list => list.Value.ManualText);
        var knownStrategy = ProfileValidation.IsStrategyFileName(strategyFile) && installation.Strategies.Any(
            strategy => string.Equals(strategy.FileName, strategyFile, StringComparison.OrdinalIgnoreCase));
        var strategy = knownStrategy ? ListTransaction.Read(Path.Combine(installation.Directory, strategyFile!), 256 * 1024) : null;
        if (strategy is null)
            warnings.Add("Для применения выберите доступную стратегию Flowseal.");
        var plan = await FlowsealConfigurationPreview.BuildPlanAsync(installation, strategyFile, profiles, token,
            baseline, strategy is null ? null : Encoding.UTF8.GetString(strategy));
        warnings.AddRange(plan.Warnings);
        var details = new List<ConfigurationFilePlan>();
        var edits = new List<ListEdit>();
        foreach (var file in plan.Files)
        {
            var list = lists[file.FileName];
            var desired = file.Additions;
            var additions = desired.Except(list.Owned, StringComparer.OrdinalIgnoreCase).ToArray();
            var removals = list.Owned.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray();
            details.Add(file with { Exists = list.Original is not null, ExistingCount = file.ExistingCount + list.Owned.Length,
                Additions = additions, Removals = removals });
            if (additions.Length + removals.Length > 0)
                edits.Add(new(file.FileName, list.Original, list.WithEntries(desired)));
        }
        var revision = Stamp(new { Root = Path.GetFullPath(installation.Directory), Strategy = strategyFile,
            StrategyHash = ListTransaction.Hash(strategy), Files = files.Select(f => ListTransaction.Hash(f.Value)),
            Details = details, Journal = journal?.Id });
        return new(new(details, warnings) { CanApply = edits.Count > 0 && strategy is not null, CanUndo = canUndo,
            Revision = revision, UndoRevision = undoStamp }, edits.ToArray());
    }

    private static string UndoStamp(ZapretInstallation installation, string state, ListJournal journal,
        IReadOnlyDictionary<string, byte[]?> files) => Stamp(new { Root = Path.GetFullPath(installation.Directory),
            State = state, Journal = journal.Id, Files = journal.Files.Select(f => ListTransaction.Hash(files[f.Name])) });
    private static string Stamp<T>(T data) => ListTransaction.Hash(JsonSerializer.SerializeToUtf8Bytes(data));
    private static InvalidOperationException Stale() =>
        new("Списки, стратегия или набор решений изменились. Обновите предпросмотр.");
}
