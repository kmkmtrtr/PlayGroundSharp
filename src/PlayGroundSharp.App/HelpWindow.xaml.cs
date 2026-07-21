using System.Windows;
using System.Windows.Input;

namespace PlayGroundSharp.App;

public partial class HelpWindow : Window
{
    public HelpWindow(AppLanguageMode languageMode)
    {
        InitializeComponent();
        DataContext = new HelpViewModel(languageMode);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
