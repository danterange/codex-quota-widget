using System.IO;

namespace CodexQuotaWidget.Services;

/// <summary>查找真实 CLI，兼容桌面应用按版本散列解压的安装目录。</summary>
internal static class CodexExecutableLocator
{
    /// <summary>优先原生程序，最后才使用可能缺少 npm 可选依赖的启动脚本。</summary>
    internal static string? Find(string? configuredPath = null, string? searchPath = null, string? desktopBin = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var directories = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"'))
            .Where(path => Path.IsPathFullyQualified(path))
            .ToArray();
        var native = directories.Select(path => Path.Combine(path, "codex.exe")).FirstOrDefault(File.Exists);
        if (native is not null)
        {
            return native;
        }

        var bin = desktopBin ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var flatPath = Path.Combine(bin, "codex.exe");
        if (File.Exists(flatPath))
        {
            return flatPath;
        }

        try
        {
            // 只检查官方桌面缓存的下一层，避免依赖代理注入的 PATH 或硬编码版本散列。
            if (Directory.Exists(bin))
            {
                native = Directory.GetDirectories(bin)
                    .Select(directory => Path.Combine(directory, "codex.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (native is not null)
                {
                    return native;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 目录不可访问时仍允许使用用户明确安装到 PATH 的 CLI。
        }

        return directories.Select(path => Path.Combine(path, "codex.cmd")).FirstOrDefault(File.Exists);
    }
}
