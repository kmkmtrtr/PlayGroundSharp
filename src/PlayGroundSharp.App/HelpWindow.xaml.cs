using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PlayGroundSharp.App;

public partial class HelpWindow : Window
{
    public HelpWindow(AppLanguageMode languageMode)
    {
        InitializeComponent();
        ApplyLanguage(languageMode);
    }

    public void ApplyLanguage(AppLanguageMode languageMode)
    {
        var selectedIndex = TopicList.SelectedIndex;
        DataContext = new HelpViewModel(languageMode);
        if (selectedIndex >= 0 && selectedIndex < TopicList.Items.Count)
            TopicList.SelectedIndex = selectedIndex;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => TopicList.Focus();

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DetailScroll.ScrollToTop, DispatcherPriority.Loaded);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
