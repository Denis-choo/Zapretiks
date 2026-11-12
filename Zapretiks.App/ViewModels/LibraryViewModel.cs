using System.Text;
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
    private InstalledProfile[]? observedProfiles;
    private ZapretInstallation? observedInstallation;
    private string? observedStrategy;

    public LibraryViewModel(Session session, IProfileTransfer transfer, IConfigurationPreview preview, ICommand openCatalog)
    {
        this.session = session;
        this.transfer = transfer;
        this.preview = preview;
        OpenCatalogCommand = openCatalog;
        ClearSelectionCommand = session.Action(() => SelectedItem = null);
        ToggleCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: false), CanChange);
        RemoveCommand = session.Command(parameter => ChangeProfileAsync(parameter, remove: true), CanChange);
        ImportCommand = session.Command(ImportAsync, () => session.CanSave);
        ExportCommand = session.Command(ExportAsync, parameter => parameter is ProfileRow || SelectedItem is not null);
        PrepareCommand = session.Command(PrepareAsync);
        session.Changed += Refresh;
    }

    public IReadOnlyList<ProfileRow> Items => items;
    public bool HasItems => Items.Count > 0;
    public ProfileRow? SelectedItem { get => selectedItem; set => Set(ref selectedItem, value); }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string PreviewText { get => previewText; private set => Set(ref previewText, value); }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand OpenCatalogCommand { get; }

    private bool CanChange(object? parameter) => session.CanSave && (parameter is ProfileRow || SelectedItem is not null);

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
            : row.IsEnabled ? $"«{row.Title}» выключен." : $"«{row.Title}» включён для подготовки.";
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
        StatusMessage = "Предпросмотр подготовлен. Файлы сборки остались без изменений.";
    }

    private void Refresh()
    {
        var state = session.State;
        if (observedProfiles != state.Profiles)
        {
            var previousId = SelectedItem?.Id;
            items = state.Profiles.Select(item => new ProfileRow(item.Profile, IsInstalled: true, item.IsEnabled)).ToArray();
            Raise(nameof(Items));
            Raise(nameof(HasItems));
            selectedItem = items.FirstOrDefault(row => row.Id == previousId);
            Raise(nameof(SelectedItem));
        }
        if (observedProfiles != state.Profiles || observedInstallation != session.Installation
            || observedStrategy != state.Settings.StrategyFile)
        {
            PreviewText = string.Empty;
            StatusMessage = null;
        }
        observedProfiles = state.Profiles;
        observedInstallation = session.Installation;
        observedStrategy = state.Settings.StrategyFile;
    }

    private static bool SameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
