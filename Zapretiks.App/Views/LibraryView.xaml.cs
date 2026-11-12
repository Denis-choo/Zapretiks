using System.Windows;
using System.Windows.Controls;

namespace Zapretiks.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e) => DetailScroll?.ScrollToTop();
}
