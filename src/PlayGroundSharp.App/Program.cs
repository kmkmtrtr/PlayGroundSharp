using PlayGroundSharp.Worker;

namespace PlayGroundSharp.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--worker")
        {
            if (!WorkerCommandLine.TryParse(args[1..], out var configuration)) return 2;
            return WorkerEntryPoint.RunAsync(configuration!).GetAwaiter().GetResult();
        }

        App? app = null;
        try
        {
            app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception error)
        {
            if (app?.HasReportedFatalError == true) return 1;
            var log = AppErrorLogger.Default.Write("Application startup", error, isTerminating: true);
            try
            {
                AppErrorDialog.Show(AppLanguageMode.Japanese, error, log, canContinue: false);
            }
            catch
            {
                // The original startup failure and logging result are more useful than a dialog failure.
            }
            return 1;
        }
    }
}
