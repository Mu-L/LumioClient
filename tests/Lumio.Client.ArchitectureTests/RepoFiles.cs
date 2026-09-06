using System.Diagnostics;

namespace Lumio.Client.ArchitectureTests;

/// <summary>
/// 仓库文件清单一律以 <c>git ls-files</c> 为准,不递归枚举工作区目录。
/// 递归枚举整个仓根会把嵌套 worktree(例如 .claude/worktrees/*)、构建产物与未跟踪文件
/// 一并算进来,导致架构断言在开发者本机假红(R-00292)。git ls-files 只列本仓索引里的
/// 已跟踪文件——嵌套 worktree 是另一个工作区、有自己的索引,天然不在结果里,因此不需要
/// 任何按路径排除的补丁,断言也不必放宽。
/// </summary>
internal static class RepoFiles
{
    private static readonly Lazy<string[]> Cache = new(Load);

    public static IReadOnlyList<string> All
    {
        get { return Cache.Value; }
    }

    /// <summary>按文件名精确匹配的已跟踪文件绝对路径。</summary>
    public static IEnumerable<string> WithFileName(string fileName)
    {
        return All.Where(p => string.Equals(System.IO.Path.GetFileName(p), fileName, StringComparison.Ordinal));
    }

    /// <summary>按扩展名匹配的已跟踪文件绝对路径。</summary>
    public static IEnumerable<string> WithExtension(string extension)
    {
        return All.Where(p => string.Equals(System.IO.Path.GetExtension(p), extension, StringComparison.Ordinal));
    }

    private static string[] Load()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git ls-files 未能启动");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // 失败必须炸,不能回落到递归枚举:静默回落等于把这道断言变成假绿。
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-files 在 {RepoRoot.Path} 退出码 {process.ExitCode}: {stderr.Trim()}");
        }

        string[] files = stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(relative => System.IO.Path.GetFullPath(System.IO.Path.Combine(RepoRoot.Path, relative)))
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException($"git ls-files 在 {RepoRoot.Path} 返回空清单");
        }

        return files;
    }
}
