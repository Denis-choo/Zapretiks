using System.Windows;
using Microsoft.Win32;

namespace Zapretiks.App;

internal sealed class DesktopDialogs
{
    public string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку распакованной сборки Flowseal" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public string? ImportProfile()
    {
        var dialog = new OpenFileDialog { Title = "Импорт профиля", Filter = "Профиль JSON (*.json)|*.json" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ExportProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт профиля",
            Filter = "Профиль JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "zapretiks-profile.json"
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
