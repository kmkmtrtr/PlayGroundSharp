using PlayGroundSharp.Worker;

namespace PlayGroundSharp.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--worker")
        {
            if (args is not ["--worker", "--pipe", { Length: > 0 } pipeName]) return 2;
            new WorkerHost(pipeName).RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
