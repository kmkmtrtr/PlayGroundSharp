using System.Windows;

namespace PlayGroundSharp.App;

public partial class HelpWindow : Window
{
    public HelpWindow(AppLanguageMode languageMode)
    {
        InitializeComponent();
        DataContext = new HelpViewModel(languageMode);
    }
}
