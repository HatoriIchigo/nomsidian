using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nomsidian;

public static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            AttachConsole(AttachParentProcess);
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Console.WriteLine($"nomu {version}");
            return 0;
        }

        var targetDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);

        if (!Directory.Exists(targetDirectory))
        {
            AttachConsole(AttachParentProcess);
            Console.Error.WriteLine($"エラー: ディレクトリが見つかりません: {targetDirectory}");
            return 1;
        }

        var app = new App();
        return app.Run(new MainWindow(targetDirectory));
    }
}
