using System;
using System.Linq;
using Avalonia;

namespace CertConvert;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // macOS can pass process-serial-number style arguments to bundled apps; ignore them.
        var cliArgs = args.Where(a => !a.StartsWith("-psn", StringComparison.Ordinal)).ToArray();

        if (cliArgs.Length > 0)
            return Cli.CliRunner.Run(cliArgs);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
