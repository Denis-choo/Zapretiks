using System.Text;
using System.IO;
using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class LibraryViewModel : ViewModel
{
    private readonly Session session;
    private readonly IProfileTransfer transfer;
    private readonly IConfigurationPreview preview;
    private IReadOnlyList<ProfileRow> items = [];
    private ProfileRow? selectedItem;
    private string? statusMessage;
    private string previewText = string.Empty;
    private ConfigurationPlan? previewPlan;
    private InstalledProfile[]? observedProfiles;
    private ZapretInstallation? observedInstallation;
    private string? observedStrategy;
    private ProfileEditorViewModel? editor;
    private string? editorError;
    private bool isSaving;
    private bool previewRequested;
    private bool workingWithFiles;

    public LibraryViewModel(Session session, IProfileTransfer transfer, IConfigurationPreview preview, ICommand openCatalog)
    {
        this.session = session;
        this.transfer = transfer;
        this.preview = preview;
        OpenCatalogCommand = openCatalog;
        ToggleCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: false), CanChange);
        RemoveCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: true), CanChange);
        ImportCommand = session.Command(ImportAsync, () => session.CanSave && IsBrowsing);
        ExportCommand = session.Command(ExportAsync, parameter => IsBrowsing && (parameter is ProfileRow || SelectedItem is not null));
        PrepareCommand = session.Command(PrepareAsync, () => IsBrowsing && !workingWithFiles);
        ReviewChangesCommand = session.Command(async () =>
        {
            previewRequested = true;
            SelectedItem = null;
            Raise(nameof(ShowPreview));
            await PrepareAsync();
        }, () => IsBrowsing && !workingWithFiles && (HasItems || HasConnectedInstallation));
        ClearSelectionCommand = ReviewChangesCommand;
        ApplyCommand = session.Command(() => WriteChangesAsync(undo: false), () =>
            CanWrite && PreviewPlan?.CanApply == true);
        UndoCommand = session.Command(() => WriteChangesAsync(undo: true), () =>
            CanWrite && PreviewPlan?.CanUndo == true);
        CopyPreviewCommand = session.Action(() =>
        {
            if (PreviewPlan is null)
                return;
            System.Windows.Clipboard.SetText(PreviewText);
            StatusMessage = "Отчёт скопирован.";
        });
        CreateCommand = session.Command(() => OpenEditor(new ProfileEditorViewModel()), () => session.CanSave && IsBrowsing);
        EditCommand = session.Command(() => OpenEditor(new ProfileEditorViewModel(SelectedItem!.Profile)),
            () => session.CanSave && IsBrowsing && CanEditSelected);
        SaveEditorCommand = session.Command(SaveEditorAsync, () => Editor is not null && !IsSaving);
        CancelEditorCommand = session.Command(CancelEditorAsync, () => Editor is not null && !IsSaving);
        session.Changed += Refresh;
    }

    public IReadOnlyList<ProfileRow> Items => items;
    public bool HasItems => Items.Count > 0;
    public bool HasSelectedProfile => SelectedItem is not null;
    public bool ShowPreview => SelectedItem is null && (HasItems || previewRequested && HasConnectedInstallation);
    private bool CanWrite => preview is IConfigurationEditor && IsBrowsing && !workingWithFiles && HasConnectedInstallation;
    public int CalculationProfileCount => Items.Count(item => item.IsEnabled && !item.IsDemo);
    public string CalculationSelectionHint => CalculationProfileCount == 0
        ? "Решения не выбраны. Можно убрать ранее добавленные ими адреса."
        : $"Выбрано решений: {CalculationProfileCount}.";
    public bool HasConnectedInstallation => session.Installation is { IsValid: true };
    public ProfileRow? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (IsBrowsing && Set(ref selectedItem, value))
            {
                previewRequested = value is null;
                Raise(nameof(CanEditSelected));
                Raise(nameof(HasSelectedProfile));
                Raise(nameof(ShowPreview));
            }
        }
    }
    public ProfileEditorViewModel? Editor { get => editor; private set => Set(ref editor, value); }
    public bool IsEditing => Editor is not null;
    public bool IsBrowsing => !IsEditing;
    public bool CanEditSelected => SelectedItem is { } row && ProfileEditorViewModel.CanEdit(row.Profile);
    public string? EditorError { get => editorError; private set => Set(ref editorError, value); }
    public bool IsSaving { get => isSaving; private set { if (Set(ref isSaving, value)) Raise(nameof(IsEditorEnabled)); } }
    public bool IsEditorEnabled => !IsSaving;
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string PreviewText { get => previewText; private set => Set(ref previewText, value); }
    public ConfigurationPlan? PreviewPlan
    {
        get => previewPlan;
        private set
        {
            if (Set(ref previewPlan, value))
            {
                Raise(nameof(PreviewSummary));
                Raise(nameof(PreviewChanges));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public string PreviewSummary
    {
        get
        {
            if (PreviewPlan is not { Files.Count: > 0 } plan)
                return string.Empty;
            var additions = plan.Files.Sum(file => file.Additions.Count);
            var removals = plan.Files.Sum(file => file.Removals.Count);
            return additions + removals == 0 ? "Изменений в списках нет" : $"Добавится адресов: {additions} · Уберётся: {removals}";
        }
    }
    public IReadOnlyList<PreviewChangeGroup> PreviewChanges => PreviewPlan?.Files
        .Where(file => file.Additions.Count + file.Removals.Count > 0)
        .Select(file => new PreviewChangeGroup(file.FileName switch
        {
            "list-general-user.txt" => "Сайты",
            "list-exclude-user.txt" => "Исключения сайтов",
            "ipset-all.txt" => "IP-адреса и подсети",
            "ipset-exclude-user.txt" => "Исключения IP",
            _ => "Другие адреса"
        }, file.Additions, file.Removals))
        .ToArray() ?? [];
    public ICommand CopyPreviewCommand { get; }
    public ICommand ChooseFolderCommand => session.ChooseFolderCommand;
    public ICommand ClearSelectionCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand ReviewChangesCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand OpenCatalogCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveEditorCommand { get; }
    public ICommand CancelEditorCommand { get; }

    private bool CanChange(object? parameter) => session.CanSave && IsBrowsing && (parameter is ProfileRow || SelectedItem is not null);

    private Task OpenEditor(ProfileEditorViewModel draft)
    {
        EditorError = null;
        Editor = draft;
        RaiseEditingState();
        return Task.CompletedTask;
    }

    private Task CancelEditorAsync()
    {
        Editor = null;
        EditorError = null;
        RaiseEditingState();
        return Task.CompletedTask;
    }

    private async Task SaveEditorAsync()
    {
        var draft = Editor;
        if (draft is null)
            return;

        EditorError = null;
        IsSaving = true;
        try
        {
            var profile = draft.Validate();
            await session.UpdateAsync(state =>
            {
                if (draft.OriginalProfile is null)
                {
                    if (state.Profiles.Any(item => SameId(item.Profile.Id, profile.Id)))
                        throw new InvalidOperationException("Такое решение уже есть в библиотеке.");
                    return state with { Profiles = [.. state.Profiles, new InstalledProfile(profile, IsEnabled: false)] };
                }

                var existing = state.Profiles.FirstOrDefault(item => SameId(item.Profile.Id, draft.OriginalProfile.Id));
                if (existing is null || existing.Profile != draft.OriginalProfile || !ProfileEditorViewModel.CanEdit(existing.Profile))
                    throw new InvalidOperationException("Решение изменилось во время редактирования. Скопируйте введённые данные и откройте решение заново.");

                return state with
                {
                    Profiles = state.Profiles.Select(item => SameId(item.Profile.Id, profile.Id)
                        ? item with { Profile = profile } : item).ToArray()
                };
            });
            Editor = null;
            SelectedItem = Items.First(item => SameId(item.Id, profile.Id));
            StatusMessage = draft.IsNew ? "Решение сохранено. Выберите его для установки, чтобы проверить адреса." : "Решение сохранено.";
            if (draft.ConvertedUrlCount > 0)
                StatusMessage += $" Из ссылок сохранены только адреса сайтов ({draft.ConvertedUrlCount}).";
            RaiseEditingState();
        }
        catch (Exception error)
        {
            EditorError = error switch
            {
                InvalidDataException or InvalidOperationException or ArgumentException => error.Message,
                UnauthorizedAccessException => "Не удалось сохранить решение. Проверьте доступ к папке данных.",
                IOException => "Не удалось сохранить решение. Проверьте доступность папки данных и попробуйте ещё раз.",
                _ => "Не удалось сохранить решение. Подробности есть в журнале приложения."
            };
            if (error is not (InvalidDataException or InvalidOperationException or ArgumentException))
                session.Report(error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void RaiseEditingState()
    {
        Raise(nameof(IsEditing));
        Raise(nameof(IsBrowsing));
        Raise(nameof(CanEditSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ChangeProfileAsync(object? parameter, bool remove)
    {
        var row = parameter as ProfileRow ?? SelectedItem;
        if (row is null)
            return;

        await session.UpdateAsync(state => state with
        {
            Profiles = remove
                ? state.Profiles.Where(item => !SameId(row.Id, item.Profile.Id)).ToArray()
                : state.Profiles.Select(item => SameId(row.Id, item.Profile.Id)
                    ? item with { IsEnabled = !item.IsEnabled } : item).ToArray()
        });
        if (remove && !row.IsDemo && HasConnectedInstallation)
        {
            previewRequested = true;
            SelectedItem = null;
            Raise(nameof(ShowPreview));
            await PrepareAsync();
        }
        StatusMessage = remove ? $"Решение «{row.Title}» удалено из библиотеки."
            : row.IsEnabled ? "Выбор снят. Проверьте изменения, чтобы убрать его адреса из сборки."
            : "Решение выбрано. Проверьте изменения перед установкой.";
    }

    private async Task ImportAsync()
    {
        var path = session.Dialogs.ImportProfile();
        if (path is null)
            return;
        var profile = await transfer.ImportAsync(path);
        await session.UpdateAsync(state =>
        {
            if (state.Profiles.Any(item => SameId(profile.Id, item.Profile.Id)))
                throw new InvalidOperationException("Решение с таким идентификатором уже есть в библиотеке.");
            return state with { Profiles = [.. state.Profiles, new InstalledProfile(profile, IsEnabled: false)] };
        });
        SelectedItem = Items.First(item => SameId(item.Id, profile.Id));
        StatusMessage = $"Решение «{profile.Name}» импортировано.";
    }

    private async Task ExportAsync(object? parameter)
    {
        var row = parameter as ProfileRow ?? SelectedItem;
        if (row is null)
            return;
        var path = session.Dialogs.ExportProfile();
        if (path is null)
            return;
        await transfer.ExportAsync(path, row.Profile);
        StatusMessage = $"Решение «{row.Title}» экспортировано.";
    }

    private async Task PrepareAsync()
    {
        if (workingWithFiles) return;
        workingWithFiles = true;
        try
        {
            PreviewPlan = null;
            StatusMessage = "Проверяем списки…";
            await RefreshPlanAsync();
            StatusMessage = null;
        }
        finally
        {
            workingWithFiles = false;
            if (PreviewPlan is null) StatusMessage = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task WriteChangesAsync(bool undo)
    {
        if (preview is not IConfigurationEditor writer || PreviewPlan is not { } reviewed || !CanWrite)
            return;
        workingWithFiles = true;
        StatusMessage = undo ? "Восстанавливаем списки…" : "Записываем изменения…";
        try
        {
            await session.RunExclusiveAsync(async () =>
            {
                if (!ReferenceEquals(PreviewPlan, reviewed) || session.Installation is not { IsValid: true } installation)
                    throw new InvalidOperationException("Параметры изменились. Проверьте списки ещё раз.");
                if (undo)
                    await writer.UndoAsync(installation, reviewed);
                else
                    await writer.ApplyAsync(installation, session.State.Settings.StrategyFile, session.State.Profiles, reviewed);
                PreviewPlan = null;
                StatusMessage = undo ? "Последнее изменение файлов отменено. Выбор решений сохранён."
                    : "Изменения записаны в списки.";
                StatusMessage += " Если zapret уже запущен, перезапустите его для загрузки списков.";
                await RefreshPlanAsync();
            });
        }
        catch
        {
            PreviewPlan = null;
            StatusMessage = "Обновите список изменений перед следующей операцией.";
            throw;
        }
        finally
        {
            workingWithFiles = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RefreshPlanAsync()
    {
        var state = session.State;
        var installation = session.Installation;
        var plan = await preview.CreateAsync(installation, state.Settings.StrategyFile, state.Profiles);
        if (session.State.Profiles != state.Profiles || installation != session.Installation
            || state.Settings.StrategyFile != session.State.Settings.StrategyFile)
            return;

        var text = new StringBuilder();
        foreach (var file in plan.Files)
        {
            text.AppendLine(file.FileName);
            text.AppendLine(file.Exists ? $"Записей сейчас: {file.ExistingCount}. Предложено: {file.Additions.Count}."
                : $"Файл отсутствует. Предложено записей: {file.Additions.Count}.");
            foreach (var entry in file.Additions)
                text.AppendLine($"+ {entry}");
            foreach (var entry in file.Removals)
                text.AppendLine($"− {entry}");
            text.AppendLine();
        }
        foreach (var warning in plan.Warnings)
            text.AppendLine($"• {warning}");
        PreviewText = text.Length == 0 ? "Для текущего набора изменений нет." : text.ToString().TrimEnd();
        PreviewPlan = plan;
    }

    private void Refresh()
    {
        var state = session.State;
        if (observedProfiles != state.Profiles)
        {
            var previousId = SelectedItem?.Id;
            var preservePreview = previewRequested;
            items = state.Profiles.Select(item => new ProfileRow(item.Profile, IsInstalled: true, item.IsEnabled)).ToArray();
            Raise(nameof(Items));
            Raise(nameof(HasItems));
            selectedItem = items.FirstOrDefault(row => row.Id == previousId)
                ?? (preservePreview ? null : items.FirstOrDefault());
            previewRequested = preservePreview;
            Raise(nameof(SelectedItem));
            Raise(nameof(CanEditSelected));
            Raise(nameof(HasSelectedProfile));
            Raise(nameof(ShowPreview));
            Raise(nameof(CalculationProfileCount));
            Raise(nameof(CalculationSelectionHint));
        }
        if (observedProfiles != state.Profiles || observedInstallation != session.Installation
            || observedStrategy != state.Settings.StrategyFile)
        {
            PreviewText = string.Empty;
            PreviewPlan = null;
            StatusMessage = null;
        }
        Raise(nameof(HasConnectedInstallation));
        Raise(nameof(ShowPreview));
        observedProfiles = state.Profiles;
        observedInstallation = session.Installation;
        observedStrategy = state.Settings.StrategyFile;
    }

    private static bool SameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

internal sealed record PreviewChangeGroup(string Label, IReadOnlyList<string> Additions, IReadOnlyList<string> Removals)
{
    public string Lines => string.Join(Environment.NewLine,
        Additions.Select(address => $"+ {address}").Concat(Removals.Select(address => $"− {address}")));
}
