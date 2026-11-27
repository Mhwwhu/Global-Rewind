using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModBuild;

/// <summary>
/// 仓库自带的跨平台构建脚本：探测游戏目录 → dotnet build → 部署到 &lt;游戏&gt;/mods/&lt;Mod&gt;/。
///
/// 只依赖 .NET SDK，因此 Windows / Linux / macOS 共用同一份实现；
/// build.sh、build.ps1、build.cmd 只是调用它的薄壳。
/// 仓库根 = 本文件往上两级（即包含 &lt;ModName&gt;.csproj 的目录），模组名取目录名。
///
/// 游戏路径不需要手工配：按 Steam 默认安装位置 + libraryfolders.vdf 里的库目录自动找，
/// 也可以用 --game / 环境变量 STS2_GAME_DIR 显式指定。
/// </summary>
internal static class Program
{
    private const string GameFolderName = "Slay the Spire 2";
    private const string WorkshopAppId = "2868840";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (BuildError error)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("!! " + error.Message);
            return error.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("!! 未预期的错误：" + error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Options options = Options.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        string repoRoot = FindRepoRoot(out string mod);
        string project = Path.Combine(repoRoot, mod + ".csproj");
        if (!File.Exists(project))
        {
            throw new BuildError("找不到模组工程：" + project, 1);
        }

        string gameDir = FindGameDir(options.GameDir);
        string dataDir = FindGameDataDir(gameDir, options.GameDataDir);
        string baseLibDir = FindBaseLibDir(gameDir, options.BaseLibDir);

        Console.WriteLine("==> 模组：  " + mod);
        Console.WriteLine("==> 游戏：  " + gameDir);
        Console.WriteLine("==> 数据：  " + dataDir);
        Console.WriteLine("==> BaseLib：" + (baseLibDir ?? "未找到（只有依赖它的模组才需要）"));

        Console.WriteLine();
        Console.WriteLine($"==> build ({options.Configuration})");
        int exitCode = Build(project, repoRoot, options.Configuration, gameDir, dataDir, baseLibDir);
        if (exitCode != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"!! 编译失败（dotnet build 退出码 {exitCode}）");
            return exitCode;
        }

        if (options.NoDeploy)
        {
            Console.WriteLine("==> 跳过部署（--no-deploy）");
            Console.WriteLine();
            Console.WriteLine("==> 完成（只编译，没有部署）。");
            return 0;
        }

        Deploy(repoRoot, gameDir, mod, options.Configuration);

        Console.WriteLine();
        Console.WriteLine("==> 完成。重启游戏后生效。");
        return 0;
    }

    // ---------------------------------------------------------------- 构建 / 部署

    private static int Build(string project, string repoRoot, string configuration,
                             string gameDir, string dataDir, string baseLibDir)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("m");
        startInfo.ArgumentList.Add("-nologo");
        // 路径只在命令行注入，仓库里不留本机的绝对路径。
        startInfo.ArgumentList.Add("-p:GameDir=" + gameDir);
        startInfo.ArgumentList.Add("-p:GameDataDir=" + dataDir);
        if (!string.IsNullOrWhiteSpace(baseLibDir))
        {
            startInfo.ArgumentList.Add("-p:BaseLibDir=" + baseLibDir);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new BuildError("无法启动 dotnet：请确认已安装 .NET SDK 9+ 并加入 PATH", 1);

        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Deploy(string repoRoot, string gameDir, string mod, string configuration)
    {
        string builtDll = Path.Combine(repoRoot, "bin", configuration, mod + ".dll");
        string builtManifest = Path.Combine(repoRoot, mod + ".json");

        string destDir = Path.Combine(gameDir, "mods", mod);
        Console.WriteLine($"==> deploy -> {destDir}");
        Directory.CreateDirectory(destDir);

        CopyOrThrow(builtDll, Path.Combine(destDir, mod + ".dll"));
        if (File.Exists(builtManifest))
        {
            CopyOrThrow(builtManifest, Path.Combine(destDir, mod + ".json"));
        }
        else
        {
            Console.WriteLine($"    （没有 {mod}.json，跳过清单）");
        }
    }

    private static void CopyOrThrow(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new BuildError("构建产物不存在：" + source, 2);
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 游戏运行时 Windows 会锁住已加载的 DLL；必须显式失败，
            // 否则脚本“看起来成功”、实际还在跑旧版本。
            throw new BuildError(
                $"部署失败：{destination} 写不进去。\n" +
                "   最常见的原因是游戏还在运行，进程锁住了已加载的 DLL。\n" +
                "   请完全退出 SlayTheSpire2.exe 后重跑本命令。\n" +
                "   原文：" + error.Message, 2);
        }
    }

    // ---------------------------------------------------------------- 仓库

    private static string FindRepoRoot(out string mod)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo current = new DirectoryInfo(start);
            while (current != null)
            {
                // 仓库根 = 顶层只有一个模组工程、且旁边有同名清单的那个目录。
                // 不按目录名判断：`git clone` 出来的目录名是仓库名（Global-Rewind），和模组名不同；
                // 也不能只看 csproj —— build/ 下就有 Build.csproj。
                string[] projects = Directory.GetFiles(current.FullName, "*.csproj", SearchOption.TopDirectoryOnly);
                if (projects.Length == 1)
                {
                    string stem = Path.GetFileNameWithoutExtension(projects[0]);
                    if (File.Exists(Path.Combine(current.FullName, stem + ".json")))
                    {
                        mod = stem;
                        return current.FullName;
                    }
                }

                current = current.Parent;
            }
        }

        mod = null;
        throw new BuildError(
            "找不到仓库根目录（需要仓库根下有一个 <ModName>.csproj）。\n" +
            "   请在仓库内运行：dotnet run --project build", 1);
    }

    // ---------------------------------------------------------------- 游戏目录

    private static string FindGameDir(string explicitDir)
    {
        string fromEnvironment = FirstNonEmpty(
            Environment.GetEnvironmentVariable("STS2_GAME_DIR"),
            Environment.GetEnvironmentVariable("GAME"));

        foreach (string raw in new[] { explicitDir, fromEnvironment })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string dir = NormalizePath(raw);
            if (!Directory.Exists(dir))
            {
                throw new BuildError("游戏目录不存在：" + dir, 1);
            }

            if (!LooksLikeGameDir(dir))
            {
                throw new BuildError(
                    $"这个目录里没有 data_sts2_*：{dir}\n" +
                    "   它应该指向 Slay the Spire 2 的安装目录（含 SlayTheSpire2.exe 与 data_sts2_* 子目录）。", 1);
            }

            return dir;
        }

        List<string> tried = new List<string>();
        foreach (string candidate in CandidateGameDirs())
        {
            tried.Add(candidate);
            if (LooksLikeGameDir(candidate))
            {
                return candidate;
            }
        }

        throw new BuildError(
            "没有自动找到游戏目录。请用 --game 或环境变量 STS2_GAME_DIR 指定，例如：\n" +
            "   bash build.sh --game \"D:/Steam/steamapps/common/Slay the Spire 2\"\n" +
            "已尝试：\n   " + string.Join("\n   ", tried), 1);
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.GetDirectories(dir, "data_sts2_*").Length > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidateGameDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> steamRoots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            steamRoots.Add("C:/Program Files (x86)/Steam");
            steamRoots.Add("C:/Program Files/Steam");
            steamRoots.Add("D:/Steam");
            steamRoots.Add("D:/3rd-party-software/Steam");
        }
        else if (OperatingSystem.IsMacOS())
        {
            steamRoots.Add(Path.Combine(home, "Library/Application Support/Steam"));
        }
        else
        {
            steamRoots.Add(Path.Combine(home, ".local/share/Steam"));
            steamRoots.Add(Path.Combine(home, ".steam/steam"));
            steamRoots.Add(Path.Combine(home, ".steam/root"));
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string steamRoot in steamRoots)
        {
            string direct = Path.GetFullPath(Path.Combine(steamRoot, "steamapps", "common", GameFolderName));
            if (seen.Add(direct))
            {
                yield return direct;
            }
        }

        // 多磁盘 / 自定义库：把 libraryfolders.vdf 里登记的每条库都算进来。
        foreach (string steamRoot in steamRoots)
        {
            foreach (string library in ReadSteamLibraries(steamRoot))
            {
                string candidate = Path.GetFullPath(Path.Combine(library, "steamapps", "common", GameFolderName));
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            string path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static string FindGameDataDir(string gameDir, string explicitDir)
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("STS2_GAME_DATA_DIR");

        foreach (string raw in new[] { explicitDir, fromEnvironment })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string dir = NormalizePath(raw);
            if (!File.Exists(Path.Combine(dir, "sts2.dll")))
            {
                throw new BuildError("这个数据目录里没有 sts2.dll：" + dir, 1);
            }

            return dir;
        }

        string[] candidates = Directory.GetDirectories(gameDir, "data_sts2_*")
            .Where(dir => File.Exists(Path.Combine(dir, "sts2.dll")))
            .OrderByDescending(dir => MatchesCurrentPlatform(Path.GetFileName(dir)))
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new BuildError("游戏目录里找不到 data_sts2_*/sts2.dll：" + gameDir, 1);
        }

        return candidates[0];
    }

    private static bool MatchesCurrentPlatform(string dirName)
    {
        if (OperatingSystem.IsWindows())
        {
            return dirName.Contains("windows", StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return dirName.Contains("macos", StringComparison.OrdinalIgnoreCase)
                || dirName.Contains("osx", StringComparison.OrdinalIgnoreCase);
        }

        return dirName.Contains("linux", StringComparison.OrdinalIgnoreCase)
            || dirName.Contains("bsd", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindBaseLibDir(string gameDir, string explicitDir)
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("STS2_BASELIB_DIR");

        foreach (string raw in new[] { explicitDir, fromEnvironment })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string dir = NormalizePath(raw);
            if (!File.Exists(Path.Combine(dir, "BaseLib.dll")))
            {
                throw new BuildError("这个 BaseLib 目录里没有 BaseLib.dll：" + dir, 1);
            }

            return dir;
        }

        // 创意工坊：<...>/steamapps/workshop/content/2868840/<itemId>/BaseLib
        // gameDir 是 <...>/steamapps/common/Slay the Spire 2，所以往上两级才是 steamapps。
        string workshop = NormalizePath(Path.Combine(gameDir, "..", "..", "workshop", "content", WorkshopAppId));
        if (!Directory.Exists(workshop))
        {
            return null;
        }

        foreach (string item in Directory.GetDirectories(workshop).OrderBy(dir => dir, StringComparer.Ordinal))
        {
            string dir = Path.Combine(item, "BaseLib");
            if (File.Exists(Path.Combine(dir, "BaseLib.dll")))
            {
                return dir;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- 杂项

    private static string NormalizePath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path.Length <= 2 ? home : Path.Combine(home, path.Substring(2));
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd('/', '\\') : full;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"构建脚本（跨平台，只依赖 .NET SDK 9+）

用法：
  bash build.sh [选项]                 # Linux / macOS / Git Bash
  powershell -File build.ps1 [选项]     # Windows PowerShell
  build.cmd [选项]                      # Windows cmd
  dotnet run --project build -- [选项]  # 直接调用

选项：
  -c, --configuration <配置>   构建配置，默认 Release
  -g, --game <目录>           游戏根目录（含 data_sts2_*）
      --data <目录>           直接指定 data_sts2_* 目录
      --baselib <目录>        BaseLib 目录（含 BaseLib.dll）
      --no-deploy             只编译，不复制到 <游戏>/mods/
  -h, --help                  显示本帮助

环境变量：
  STS2_GAME_DIR / GAME        同 --game
  STS2_GAME_DATA_DIR          同 --data
  STS2_BASELIB_DIR            同 --baselib

退出码：0 成功 / 1 参数或路径错误 / 2 部署失败（通常是游戏没退出）");
    }

    // ---------------------------------------------------------------- 参数

    private sealed class Options
    {
        public string Configuration { get; private set; } = "Release";
        public string GameDir { get; private set; }
        public string GameDataDir { get; private set; }
        public string BaseLibDir { get; private set; }
        public bool NoDeploy { get; private set; }
        public bool ShowHelp { get; private set; }

        public static Options Parse(string[] args)
        {
            Options options = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "-c":
                    case "--configuration":
                        options.Configuration = TakeValue(args, ref i, arg);
                        break;
                    case "-g":
                    case "--game":
                        options.GameDir = TakeValue(args, ref i, arg);
                        break;
                    case "--data":
                        options.GameDataDir = TakeValue(args, ref i, arg);
                        break;
                    case "--baselib":
                        options.BaseLibDir = TakeValue(args, ref i, arg);
                        break;
                    case "--no-deploy":
                        options.NoDeploy = true;
                        break;
                    default:
                        throw new BuildError($"未知参数：{arg}（--help 查看用法；本仓库只包含一个模组，不需要写模组名）", 1);
                }
            }

            return options;
        }

        private static string TakeValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new BuildError($"{option} 需要一个值", 1);
            }

            index++;
            return args[index];
        }
    }

    private sealed class BuildError : Exception
    {
        public BuildError(string message, int exitCode) : base(message)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }
    }
}
