using System.IO;
using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class GamesViewModel : ViewModel
{
    private readonly ISteamScanner scanner;
    private IReadOnlyList<GameRow> games = [];
    private string statusMessage = "Запустите сканирование, чтобы найти установленные игры Steam.";
    private bool isScanning;
    private bool scanned;

    public GamesViewModel(Session session, ISteamScanner scanner)
    {
        this.scanner = scanner;
        ScanCommand = session.Command(ScanAsync);
    }

    public IReadOnlyList<GameRow> Games { get => games; private set { if (Set(ref games, value)) Raise(nameof(HasItems)); } }
    public bool HasItems => Games.Count > 0;
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public bool IsScanning { get => isScanning; private set => Set(ref isScanning, value); }
    public ICommand ScanCommand { get; }

    public void ScanWhenOpened()
    {
        if (!scanned && ScanCommand.CanExecute(null))
            ScanCommand.Execute(null);
    }

    private async Task ScanAsync()
    {
        IsScanning = true;
        StatusMessage = "Читаем локальные библиотеки Steam…";
        try
        {
            var result = await scanner.ScanAsync();
            Games = result.Games.Select(game => new GameRow(game)).ToArray();
            var summary = Games.Count > 0 ? $"Обнаружено игр: {Games.Count}." : "В библиотеках Steam установленных игр не найдено.";
            StatusMessage = result.SteamFound
                ? string.Join(Environment.NewLine, new[] { summary }.Concat(result.Diagnostics))
                : result.Diagnostics.FirstOrDefault() ?? "Папка Steam не обнаружена.";
            scanned = true;
        }
        catch
        {
            StatusMessage = "Не удалось завершить сканирование Steam. Можно запустить его повторно.";
            throw;
        }
        finally
        {
            IsScanning = false;
        }
    }
}

internal sealed record GameRow(SteamGame Game)
{
    public string Title => Game.Name;
    public string Platform => "Steam";
    public string InstallPath => Game.InstallPath;
    public string LibraryPath => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(InstallPath))) ?? InstallPath;
    public string AppIdLabel => $"App ID {Game.AppId}";
}
