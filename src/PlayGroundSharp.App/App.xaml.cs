using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayGroundSharp.App;

public partial class App : Application
{
    private static int currentLanguageMode = (int)AppLanguageMode.Japanese;
    private int errorDialogVisible;
    private int fatalErrorReported;

    internal bool HasReportedFatalError => Volatile.Read(ref fatalErrorReported) != 0;

    public App()
    {
        ScrollWheelRouter.Register();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        Exit += App_Exit;
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        var canContinue = AppErrorPolicy.CanContinue(e.Exception);
        var log = AppErrorLogger.Default.Write(
            "UI Dispatcher",
            e.Exception,
            isTerminating: !canContinue);
        if (!canContinue) Volatile.Write(ref fatalErrorReported, 1);
        e.Handled = canContinue;
        ShowErrorSafely(e.Exception, log, canContinue);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var error = e.ExceptionObject as Exception ?? new InvalidOperationException(
            $"Unhandled non-exception object: {e.ExceptionObject}");
        var log = AppErrorLogger.Default.Write("AppDomain", error, e.IsTerminating);
        if (e.IsTerminating) Volatile.Write(ref fatalErrorReported, 1);
        ShowErrorSafely(error, log, canContinue: !e.IsTerminating);
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        var log = AppErrorLogger.Default.Write(
            "Unobserved task",
            e.Exception,
            isTerminating: false);
        e.SetObserved();
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke(
                () => ShowErrorSafely(e.Exception, log, canContinue: true),
                DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher completed shutdown after the state check.
        }
    }

    private void ShowErrorSafely(
        Exception error,
        AppErrorLogResult log,
        bool canContinue)
    {
        if (Interlocked.Exchange(ref errorDialogVisible, 1) != 0) return;
        try
        {
            var owner = Dispatcher.CheckAccess() ? MainWindow : null;
            AppErrorDialog.Show(ResolveLanguageMode(), error, log, canContinue, owner);
        }
        catch (Exception dialogError)
        {
            _ = AppErrorLogger.Default.Write(
                "Error dialog",
                dialogError,
                isTerminating: false);
        }
        finally
        {
            Volatile.Write(ref errorDialogVisible, 0);
        }
    }

    private static AppLanguageMode ResolveLanguageMode() =>
        (AppLanguageMode)Volatile.Read(ref currentLanguageMode);

    private void App_Exit(object sender, ExitEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
    }

    public static void ApplyLanguage(AppLanguageMode mode)
    {
        Volatile.Write(ref currentLanguageMode, (int)mode);
        if (Current is null) return;
        foreach (var (key, value) in AppLocalization.Resources(mode)) Current.Resources[key] = value;
    }

    public static void ApplyTheme(AppThemeMode mode)
    {
        if (Current is null) return;
        var colors = mode == AppThemeMode.Dark
            ? new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#1E1E1E", ["PanelBrush"] = "#252526",
                ["BorderBrush"] = "#3F3F46", ["ForegroundBrush"] = "#F3F4F6",
                ["MutedBrush"] = "#A1A1AA", ["AccentBrush"] = "#60A5FA",
                ["InputBrush"] = "#18181B", ["DrawerBrush"] = "#252526",
                ["ErrorBrush"] = "#F87171", ["WarningBrush"] = "#FBBF24",
                ["HoverBrush"] = "#2D3440", ["SelectionBrush"] = "#1E3A5F",
                ["AccentHoverBrush"] = "#3B82F6", ["OverlayBrush"] = "#2B2B2F",
                ["ExplorerNamespaceBrush"] = "#C4B5FD", ["ExplorerClassBrush"] = "#93C5FD",
                ["ExplorerRecordBrush"] = "#67E8F9", ["ExplorerInterfaceBrush"] = "#6EE7B7",
                ["ExplorerStructBrush"] = "#FCD34D", ["ExplorerEnumBrush"] = "#D8B4FE",
                ["ExplorerDelegateBrush"] = "#F9A8D4", ["ExplorerMethodBrush"] = "#5EEAD4"
            }
            : new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#FFFFFF", ["PanelBrush"] = "#F8F9FA",
                ["BorderBrush"] = "#DADCE0", ["ForegroundBrush"] = "#202124",
                ["MutedBrush"] = "#5F6368", ["AccentBrush"] = "#1967D2",
                ["InputBrush"] = "#FFFFFF", ["DrawerBrush"] = "#F8F9FA",
                ["ErrorBrush"] = "#B3261E", ["WarningBrush"] = "#8B6914",
                ["HoverBrush"] = "#EDF3FF", ["SelectionBrush"] = "#DBEAFE",
                ["AccentHoverBrush"] = "#1D4ED8", ["OverlayBrush"] = "#FFFFFF",
                ["ExplorerNamespaceBrush"] = "#7C3AED", ["ExplorerClassBrush"] = "#2563EB",
                ["ExplorerRecordBrush"] = "#0891B2", ["ExplorerInterfaceBrush"] = "#059669",
                ["ExplorerStructBrush"] = "#D97706", ["ExplorerEnumBrush"] = "#9333EA",
                ["ExplorerDelegateBrush"] = "#DB2777", ["ExplorerMethodBrush"] = "#0F766E"
            };
        foreach (var (key, value) in colors)
            Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
}
