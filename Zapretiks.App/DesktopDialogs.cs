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
        var dialog = new OpenFileDialog { Title = "Импорт решения", Filter = "Решение JSON (*.json)|*.json" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ExportProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт решения",
            Filter = "Решение JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "zapretiks-solution.json"
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
