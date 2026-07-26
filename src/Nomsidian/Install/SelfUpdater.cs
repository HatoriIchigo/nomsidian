using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nomsidian.Install;

/// <summary>
/// nomu 自身のソース再ビルド型・自己更新。<c>nomu --update</c> の実体。
///
/// 稼働中の実行ファイルは自分自身では上書きできない（特に Windows はロック）。そこで
///   1. <see cref="Run"/>（稼働中の実行体）: リポジトリを tmp へ clone → editor バンドルをビルド →
///      self-contained single-file で publish → publish 済みの<b>新バイナリ</b>を <c>--apply-update</c>
///      モードで detached 起動し、自身は終了する。
///   2. <see cref="ApplyUpdate"/>（tmp の新バイナリ）: 旧プロセス終了を待ち、インストール先の実行体を
///      退避（.bak）→ 新バイナリで上書き → 起動検証（失敗はロールバック）→ tmp 掃除。
///
/// applier は「置換対象 exe とは別ファイル（tmp の新バイナリ）」ゆえロックに縛られず置換できる。
/// ヘルパは bat/sh ではなく本体と同一コードベースの 1 モード。参考: ai-harness-main の SelfUpdater。
/// </summary>
internal static class SelfUpdater
{
    /// <summary>更新元リポジトリ（origin）と追跡ブランチ。</summary>
    private const string RepoUrl = "https://github.com/HatoriIchigo/nomsidian";
    private const string Branch = "main";

    /// <summary>clone 後のリポジトリ内での相対パス。</summary>
    private const string CsprojRelative = "src/Nomsidian/Nomsidian.csproj";
    private const string EditorSrcRelative = "src/Nomsidian/editor-src";
    private const string WebUiRelative = "src/Nomsidian/Assets/webui";

    private const string ApplyMode = "--apply-update";

    private const int ExitOk = 0;
    private const int ExitError = 1;

    /// <summary>
    /// <c>--update</c> の入口。前提コマンドを確認し、新版を tmp へ publish して applier へハンドオフする。
    /// 既に最新（HEAD が現行バイナリと同一 sha）なら何もせず 0。ハンドオフできたら 0。前提不足や失敗は非 0。
    /// </summary>
    public static int Run()
    {
        if (!CommandExists("git"))
        {
            Console.Error.WriteLine("git が見つからない。git をインストールしてから再実行。");
            return ExitError;
        }
        if (!CommandExists("dotnet"))
        {
            Console.Error.WriteLine("dotnet が見つからない。.NET SDK をインストールしてから再実行。");
            return ExitError;
        }

        var installExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(installExe))
        {
            Console.Error.WriteLine("本体パスを特定できないため自己更新をスキップ。");
            return ExitError;
        }

        // dotnet ミュクサ経由（dotnet <dll>）だと置換すべき本体 exe を特定できない。単一ファイル実行のみ対象。
        var exeName = Path.GetFileName(installExe);
        if (string.Equals(Path.GetFileNameWithoutExtension(exeName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "`dotnet <dll>` 経由の起動では自己更新できない（単一ファイル発行の実行体で実行が必要）。スキップ。");
            return ExitError;
        }

        var tmpRoot = Path.Combine(Path.GetTempPath(), "nomu-selfupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);

        try
        {
            var repoDir = Path.Combine(tmpRoot, "nomsidian");
            Console.WriteLine($"clone: {RepoUrl} ({Branch}) -> {repoDir}");
            CloneOrUpdate(RepoUrl, Branch, repoDir);

            // 既に最新なら差し替え不要。現行バイナリに埋め込まれた sha と clone した HEAD を比較。
            var targetSha = TryGitHead(repoDir);
            var currentSha = CurrentRevision();
            if (targetSha is not null && currentSha is not null
                && targetSha.StartsWith(currentSha, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"既に最新です（{Short(currentSha)}）。更新は不要。");
                return ExitOk;
            }

            // editor バンドル（Assets/webui）はリポジトリに追跡済みだが、node があれば最新 main.js から
            // 再ビルドして確実に一致させる。node が無ければ追跡済みバンドルをそのまま使う。
            BuildEditorBundle(repoDir);

            // self-contained single-file で publish（配置先に .NET を要求しない）。
            // Content（Assets/webui）も self-extract に含め、exe 1 個の置換で完結させる。
            var csproj = Path.Combine(repoDir, ToNativePath(CsprojRelative));
            if (!File.Exists(csproj))
            {
                throw new InvalidOperationException($"csproj が見つからない: {csproj}");
            }

            var outDir = Path.Combine(tmpRoot, "out");
            var rid = RuntimeInformation.RuntimeIdentifier;
            Console.WriteLine($"publish: {csproj} (rid={rid})");
            var publishArgs = new List<string>
            {
                "publish", csproj, "-c", "Release", "-r", rid, "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:IncludeAllContentForSelfExtract=true", "-o", outDir,
            };
            if (targetSha is not null)
            {
                publishArgs.Add($"-p:SourceRevisionId={targetSha}");
            }
            RunOrThrow("dotnet", publishArgs);

            var newExe = Path.Combine(outDir, exeName);
            if (!File.Exists(newExe))
            {
                throw new InvalidOperationException($"publish 出力に実行体が無い: {newExe}");
            }

            // 発行直後の健全性検証（壊れた本体で置換に進まない）。
            if (RunExe(newExe, ["--health"]) != 0)
            {
                throw new InvalidOperationException("publish した新バイナリの起動検証に失敗。");
            }

            Console.WriteLine($"更新を適用します: {Short(currentSha) ?? "(unknown)"} -> {Short(targetSha) ?? "(new)"}");

            // 新バイナリを applier として detached 起動。自身は即終了し、実行体ロックを解放する。
            StartDetached(newExe,
            [
                ApplyMode,
                "--target", installExe,
                "--pid", Environment.ProcessId.ToString(),
                "--tmp", tmpRoot,
            ]);
            Console.WriteLine("更新をバックグラウンドで適用中。結果は実行体隣の update.log を参照。");
            // tmp はハンドオフ先が掃除するのでここでは消さない。
            tmpRoot = null;
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"更新に失敗（本体は据え置き）: {ex.Message}");
            return ExitError;
        }
        finally
        {
            if (tmpRoot is not null)
            {
                TryDeleteDirectory(tmpRoot);
            }
        }
    }

    /// <summary>
    /// <c>--apply-update</c> モード。tmp の新バイナリから動き、インストール先の実行体を安全に置換する。
    /// 引数: <c>--target &lt;install exe&gt; --pid &lt;旧プロセス&gt; --tmp &lt;作業領域&gt;</c>。
    /// GUI アプリのため daemon 再起動は行わない。結果は実行体隣の update.log へ記録する。
    /// </summary>
    public static int ApplyUpdate(string[] args)
    {
        var target = ArgValue(args, "--target");
        var pidText = ArgValue(args, "--pid");
        var tmpRoot = ArgValue(args, "--tmp");

        if (string.IsNullOrEmpty(target))
        {
            return ExitError; // 置換先不明。ログ先も定まらないため静かに失敗。
        }

        var installDir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var logPath = Path.Combine(installDir, "update.log");
        Log(logPath, $"自己更新 apply 開始 target={target}");

        var backup = target + ".bak";

        try
        {
            // 1. 旧 --update プロセスの終了を待つ（実行体ロック解放のため）。
            if (int.TryParse(pidText, out var pid))
            {
                WaitForProcessExit(pid, TimeSpan.FromSeconds(30));
            }

            // 2. 現行実行体を退避。
            File.Copy(target, backup, overwrite: true);

            // 3. 新バイナリで上書き（ロック解放前は失敗し得るためリトライ）。
            CopyWithRetry(Environment.ProcessPath!, target, TimeSpan.FromSeconds(30));
            Log(logPath, "実行体を置換。起動検証中。");

            // 4. 置換後の起動検証。失敗ならロールバック。
            if (RunExe(target, ["--health"]) != 0)
            {
                File.Copy(backup, target, overwrite: true);
                Log(logPath, "置換後の起動検証に失敗。旧実行体へロールバックした。");
                return ExitError;
            }

            Log(logPath, "自己更新に成功。");
            TryDelete(backup);
            return ExitOk;
        }
        catch (Exception ex)
        {
            Log(logPath, $"自己更新に失敗: {ex.Message}");
            try
            {
                if (File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                    Log(logPath, "旧実行体へロールバックした。");
                }
            }
            catch (Exception rollbackEx)
            {
                Log(logPath, $"ロールバックにも失敗: {rollbackEx.Message}");
            }
            return ExitError;
        }
        finally
        {
            // tmp 掃除（自分自身の exe は Windows で削除できないため best-effort）。
            if (!string.IsNullOrEmpty(tmpRoot))
            {
                TryDeleteDirectory(tmpRoot);
            }
        }
    }

    /// <summary>現行バイナリに埋め込まれたコミット sha（InformationalVersion の <c>+sha</c> 部分）。無ければ null。</summary>
    public static string? CurrentRevision()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return null;
        }
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : null;
    }

    // ---- editor バンドルのビルド ----

    /// <summary>
    /// editor-src を esbuild でバンドルし <c>Assets/webui</c> を更新する。node/npm が無ければ、
    /// リポジトリ追跡済みのバンドルをそのまま使う（警告のみ）。
    /// </summary>
    private static void BuildEditorBundle(string repoDir)
    {
        var editorSrc = Path.Combine(repoDir, ToNativePath(EditorSrcRelative));
        var webUi = Path.Combine(repoDir, ToNativePath(WebUiRelative));

        var npm = ResolveCommand("npm");
        if (!CommandExists(npm))
        {
            Console.WriteLine("npm が無いため editor 再ビルドをスキップ（追跡済みバンドルを使用）。");
            return;
        }

        // 依存の取得もバンドルも editor-src を作業ディレクトリにして実行する（node_modules 解決のため）。
        Console.WriteLine($"editor build: npm ci ({editorSrc})");
        RunOrThrow(npm, ["ci"], editorSrc);

        Directory.CreateDirectory(webUi);
        var bundleOut = Path.Combine(webUi, "bundle.js");
        Console.WriteLine("editor build: esbuild -> bundle.js");
        RunOrThrow(npm, ["exec", "--", "esbuild",
            Path.Combine("src", "main.js"), "--bundle", "--minify", $"--outfile={bundleOut}"], editorSrc);

        // editor.html は esbuild 対象外なのでコピーで反映する。
        File.Copy(Path.Combine(editorSrc, "src", "editor.html"), Path.Combine(webUi, "editor.html"), overwrite: true);
    }

    // ---- git ヘルパ ----

    /// <summary>
    /// <paramref name="repoDir"/> にリポジトリを用意する。既存（<c>.git</c> あり）なら fetch して
    /// <c>reset --hard FETCH_HEAD</c>、無ければ shallow clone。
    /// </summary>
    private static void CloneOrUpdate(string url, string branch, string repoDir)
    {
        if (Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            RunOrThrow("git", ["-C", repoDir, "fetch", "--depth", "1", "origin", branch]);
            RunOrThrow("git", ["-C", repoDir, "reset", "--hard", "FETCH_HEAD"]);
        }
        else
        {
            RunOrThrow("git", ["clone", "--depth", "1", "--branch", branch, url, repoDir]);
        }
    }

    /// <summary>clone 済みリポジトリの HEAD sha（取得失敗は null）。</summary>
    private static string? TryGitHead(string repoDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(repoDir);
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && sha.Length > 0 ? sha : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- プロセス／ファイルヘルパ ----

    private static string ToNativePath(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    private static string? Short(string? sha) =>
        sha is { Length: > 7 } ? sha[..7] : sha;

    private static string? ArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // 既に終了済み。
        }
    }

    private static void CopyWithRetry(string source, string dest, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static int RunExe(string file, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// <paramref name="file"/> を実行し、非 0 終了なら例外。出力は親コンソールへそのまま流す。
    /// <paramref name="workingDirectory"/> を渡すとそのディレクトリで実行する。
    /// </summary>
    private static void RunOrThrow(string file, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"プロセス起動に失敗: {file}");
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', args)} が終了コード {p.ExitCode} で失敗");
        }
    }

    /// <summary>
    /// Windows では npm/npx などのランチャーが <c>.cmd</c> のため、<c>UseShellExecute=false</c> では拡張子なしで
    /// 解決できない。Windows のときだけ <c>.cmd</c> を補う（git/dotnet は .exe なので対象外）。
    /// </summary>
    private static string ResolveCommand(string command) =>
        OperatingSystem.IsWindows() ? command + ".cmd" : command;

    /// <summary>親の stdio から切り離して detached 起動する。</summary>
    private static void StartDetached(string exe, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        }
        else
        {
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string logPath, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // ログ書き込み失敗は無害。
        }
        Console.WriteLine(line);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 自分自身の exe を含む tmp は Windows で消せないことがある。無害。
        }
    }
}
