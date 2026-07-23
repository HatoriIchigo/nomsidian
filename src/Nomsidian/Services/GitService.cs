using System.Diagnostics;
using System.IO;

namespace Nomsidian.Services;

/// <summary>
/// git signs（変更行ガター）用に、直近コミット(HEAD)時点のファイル内容を取得する。
/// </summary>
public static class GitService
{
    /// <summary>
    /// <paramref name="filePath"/> の HEAD 時点の内容を返す。
    /// Git リポジトリ配下でない場合は null。リポジトリ配下だが HEAD に存在しない
    /// （未追跡・新規ファイルや、コミットが1つも無い場合）は空文字列を返す。
    /// </summary>
    public static string? TryGetHeadContent(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is null || !File.Exists(filePath))
        {
            return null;
        }

        var repoRoot = RunGit(directory, "rev-parse", "--show-toplevel");
        if (repoRoot is null)
        {
            return null;
        }

        var normalizedRoot = repoRoot.Trim().Replace('/', Path.DirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(normalizedRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');

        var headContent = RunGit(directory, "show", $"HEAD:{relativePath}");
        return headContent ?? string.Empty;
    }

    private static string? RunGit(string workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
