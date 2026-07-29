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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
