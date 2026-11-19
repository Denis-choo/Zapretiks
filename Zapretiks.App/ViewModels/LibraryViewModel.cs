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

    public LibraryViewModel(Session session, IProfileTransfer transfer, IConfigurationPreview preview, ICommand openCatalog)
    {
        this.session = session;
        this.transfer = transfer;
        this.preview = preview;
        OpenCatalogCommand = openCatalog;
        ClearSelectionCommand = session.Command(() =>
        {
            previewRequested = true;
            SelectedItem = null;
            return Task.CompletedTask;
        }, () => IsBrowsing);
        ToggleCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: false), CanChange);
        RemoveCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: true), CanChange);
        ImportCommand = session.Command(ImportAsync, () => session.CanSave && IsBrowsing);
        ExportCommand = session.Command(ExportAsync, parameter => IsBrowsing && (parameter is ProfileRow || SelectedItem is not null));
        PrepareCommand = session.Command(PrepareAsync, () => IsBrowsing);
        ReviewChangesCommand = session.Command(async () =>
        {
            previewRequested = true;
            SelectedItem = null;
            await PrepareAsync();
        }, () => IsBrowsing && HasItems);
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
    public bool ShowPreview => HasItems && SelectedItem is null;
    public int CalculationProfileCount => Items.Count(item => item.IsEnabled && !item.IsDemo);
    public string CalculationSelectionHint => Items.All(item => item.IsDemo)
        ? "Для расчёта создайте профиль со своими адресами."
        : CalculationProfileCount == 0
            ? "Выберите профиль слева и включите его в расчёт."
            : $"Профилей в расчёте: {CalculationProfileCount}.";
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
            }
        }
    }
    public string PreviewSummary
    {
        get
        {
            if (PreviewPlan is not { Files.Count: > 0 } plan)
                return string.Empty;
            var count = plan.Files.Sum(file => file.Additions.Count);
            return count > 0 ? $"Будет добавлено адресов: {count}" : "Новых адресов нет";
        }
    }
    public IReadOnlyList<PreviewChangeGroup> PreviewChanges => PreviewPlan?.Files
        .Where(file => file.Additions.Count > 0)
        .Select(file => new PreviewChangeGroup(file.FileName switch
        {
            "list-general-user.txt" => "Сайты",
            "list-exclude-user.txt" => "Исключения сайтов",
            "ipset-all.txt" => "IP-адреса и подсети",
            "ipset-exclude-user.txt" => "Исключения IP",
            _ => "Другие адреса"
        }, file.Additions))
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
                        throw new InvalidOperationException("Такой профиль уже есть в библиотеке.");
                    return state with { Profiles = [.. state.Profiles, new InstalledProfile(profile, IsEnabled: false)] };
                }

                var existing = state.Profiles.FirstOrDefault(item => SameId(item.Profile.Id, draft.OriginalProfile.Id));
                if (existing is null || existing.Profile != draft.OriginalProfile || !ProfileEditorViewModel.CanEdit(existing.Profile))
                    throw new InvalidOperationException("Профиль изменился во время редактирования. Скопируйте введённые данные и откройте профиль заново.");

                return state with
                {
                    Profiles = state.Profiles.Select(item => SameId(item.Profile.Id, profile.Id)
                        ? item with { Profile = profile } : item).ToArray()
                };
            });
            Editor = null;
            SelectedItem = Items.First(item => SameId(item.Id, profile.Id));
            StatusMessage = draft.IsNew ? "Профиль создан. Включите его в расчёт, чтобы проверить изменения." : "Изменения профиля сохранены.";
            if (draft.ConvertedUrlCount > 0)
                StatusMessage += $" Из ссылок сохранены только адреса сайтов ({draft.ConvertedUrlCount}).";
            RaiseEditingState();
        }
        catch (Exception error)
        {
            EditorError = error switch
            {
                InvalidDataException or InvalidOperationException or ArgumentException => error.Message,
                UnauthorizedAccessException => "Не удалось сохранить профиль. Проверьте доступ к папке данных.",
                IOException => "Не удалось сохранить профиль. Проверьте доступность папки данных и попробуйте ещё раз.",
                _ => "Не удалось сохранить профиль. Подробности есть в журнале приложения."
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
        StatusMessage = remove ? $"«{row.Title}» удалён из библиотеки."
            : row.IsEnabled ? $"«{row.Title}» исключён из расчёта." : $"«{row.Title}» будет учтён в расчёте.";
        if (!remove && row.IsDemo)
            StatusMessage = "Демонстрационные профили помогают изучить интерфейс. Для расчёта создайте свой профиль.";
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
                throw new InvalidOperationException("Профиль с таким идентификатором уже есть в библиотеке.");
            return state with { Profiles = [.. state.Profiles, new InstalledProfile(profile, IsEnabled: false)] };
        });
        SelectedItem = Items.First(item => SameId(item.Id, profile.Id));
        StatusMessage = $"«{profile.Name}» импортирован.";
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
        StatusMessage = $"«{row.Title}» экспортирован.";
    }

    private async Task PrepareAsync()
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
            text.AppendLine();
        }
        foreach (var warning in plan.Warnings)
            text.AppendLine($"• {warning}");
        PreviewText = text.Length == 0 ? "Для текущего набора изменений нет." : text.ToString().TrimEnd();
        PreviewPlan = plan;
        StatusMessage = null;
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
        observedProfiles = state.Profiles;
        observedInstallation = session.Installation;
        observedStrategy = state.Settings.StrategyFile;
    }

    private static bool SameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

internal sealed record PreviewChangeGroup(string Label, IReadOnlyList<string> Values)
{
    public string Lines => string.Join(Environment.NewLine, Values);
}
