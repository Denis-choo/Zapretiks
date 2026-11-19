using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zapretiks.App.Views;

public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView() => InitializeComponent();

    private void OnEditorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsVisible)
                {
                    EditorScroll.ScrollToTop();
                    NameField.Focus();
                }
            });
    }
}
