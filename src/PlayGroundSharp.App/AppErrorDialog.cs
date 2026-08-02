using System.Windows;

namespace PlayGroundSharp.App;

internal static class AppErrorDialog
{
    public static string BuildMessage(
        AppLanguageMode languageMode,
        Exception error,
        AppErrorLogResult log,
        bool canContinue)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(log);
        var location = log.Path ?? AppLocalization.Text(
            languageMode,
            "Dialog.ErrorLogUnavailable",
            log.Failure ?? "unknown error");
        var key = canContinue
            ? "Dialog.UnhandledErrorContinue"
            : "Dialog.UnhandledErrorTerminate";
        return AppLocalization.Text(
            languageMode,
            key,
            error.GetType().Name,
            string.IsNullOrWhiteSpace(error.Message) ? "(no message)" : error.Message,
            location);
    }

    public static void Show(
        AppLanguageMode languageMode,
        Exception error,
        AppErrorLogResult log,
        bool canContinue,
        Window? owner = null)
    {
        var message = BuildMessage(languageMode, error, log, canContinue);
        var title = AppLocalization.Text(languageMode, "Dialog.ErrorTitle");
        if (owner is { IsVisible: true } && owner.Dispatcher.CheckAccess())
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
