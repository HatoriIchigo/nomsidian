using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Nomsidian.Config;
using Nomsidian.Install;

namespace Nomsidian;

public static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : null;

        switch (mode)
        {
            case "--version":
            case "-v":
                AttachConsole(AttachParentProcess);
                Console.WriteLine($"nomu {DisplayVersion()}");
                return 0;

            case "--update":
                // ソース再ビルド型の自己更新。GUI は起動しない。
                AttachConsole(AttachParentProcess);
                return SelfUpdater.Run();

            case "--apply-update":
                // 内部モード（ユーザ非公開）。--update が publish した tmp の新バイナリから起動される。
                AttachConsole(AttachParentProcess);
                return SelfUpdater.ApplyUpdate(args);

            case "--health":
                // 起動検証用。ランタイムが正常起動できれば 0（自己更新のロールバック判定に使う）。
                AttachConsole(AttachParentProcess);
                Console.WriteLine("nomu OK");
                return 0;
        }

        var targetDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);

        if (!Directory.Exists(targetDirectory))
        {
            AttachConsole(AttachParentProcess);
            Console.Error.WriteLine($"エラー: ディレクトリが見つかりません: {targetDirectory}");
            return 1;
        }

        NomuConfig config;
        try
        {
            config = ConfigLoader.Load(targetDirectory);
        }
        catch (ConfigLoadException ex)
        {
            MessageBox.Show(ex.Message, "nomsidian 設定エラー");
            config = new NomuConfig();
        }

        var app = new App();
        return app.Run(new MainWindow(targetDirectory, config));
    }

    /// <summary>
    /// 表示用バージョン。publish 時に <c>-p:SourceRevisionId=&lt;sha&gt;</c> が埋め込まれていれば
    /// <c>0.1.0+&lt;sha 先頭 7 桁&gt;</c> を返す（自己更新の「既に最新」判定にも使う）。無ければ数値版。
    /// </summary>
    internal static string DisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0 && informational.Length - plus - 1 > 7)
            {
                return informational[..(plus + 1 + 7)];
            }
            return informational;
        }
        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }
}
