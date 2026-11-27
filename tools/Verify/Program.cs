using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;

namespace Verify;

/// <summary>
/// 通用模组校验器：不启动游戏，直接对着真实的 sts2.dll 检查
///   · 模组程序集里的类型是否存在
///   · 每个 Harmony 补丁的目标方法是否还在、补丁方法是否存在、能否真正挂上
///   · 模组依赖的私有字段 / 公开成员是否还在
///
/// 用法：
///   dotnet run -c Release --project tools/Verify
///
/// 校验内容由仓库根的 verify/targets.json 提供；模组名取仓库目录名。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        string repoRoot = Metadata("RepoRoot");
        string gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? Metadata("GameDir");
        string baseLibDir = Environment.GetEnvironmentVariable("STS2_BASELIB_DIR") ?? Metadata("BaseLibDir");

        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            Console.WriteLine("无法确定仓库根目录（RepoRoot 元数据缺失）");
            return 1;
        }

        // 元数据里是 .../tools/Verify/../..，先规范化，否则目录名会取成 ".."。
        repoRoot = Path.GetFullPath(repoRoot);

        // 仓库根就是这个模组目录：模组名 = 目录名，校验清单在 <仓库根>/verify/targets.json。
        string mod = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string targetsPath = Path.Combine(repoRoot, "verify", "targets.json");

        if (!File.Exists(targetsPath))
        {
            Console.WriteLine($"找不到校验配置：{targetsPath}");
            return 1;
        }

        TargetFile targets;
        try
        {
            targets = JsonSerializer.Deserialize<TargetFile>(
                File.ReadAllText(targetsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e)
        {
            Console.WriteLine($"解析 targets.json 失败：{e.Message}");
            return 1;
        }

        if (targets == null || string.IsNullOrWhiteSpace(targets.Assembly))
        {
            Console.WriteLine("targets.json 缺少 assembly 字段");
            return 1;
        }

        Console.WriteLine($"== verify {mod} ==");

        // 数据目录名随平台变化（data_sts2_windows_x86_64 / data_sts2_linuxbsd_* / …），
        // 这里按“含 sts2.dll”自动挑，和构建脚本的探测规则保持一致。
        string dataDir = Environment.GetEnvironmentVariable("STS2_GAME_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "sts2.dll")))
        {
            dataDir = Directory.GetDirectories(gameDir, "data_sts2_*")
                .Where(dir => File.Exists(Path.Combine(dir, "sts2.dll")))
                .OrderByDescending(dir => MatchesCurrentPlatform(Path.GetFileName(dir)))
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            Console.WriteLine($"游戏目录里找不到 data_sts2_*/sts2.dll：{gameDir}");
            Console.WriteLine("用环境变量 STS2_GAME_DIR / STS2_GAME_DATA_DIR 指定游戏目录与数据目录");
            return 1;
        }

        string modDeployDir = Path.Combine(gameDir, "mods", mod);
        string modDll = Path.Combine(modDeployDir, targets.Assembly);

        if (!File.Exists(modDll))
        {
            Console.WriteLine($"找不到已部署的模组 DLL：{modDll}");
            Console.WriteLine("先运行 dotnet run --project build --（或 bash build.sh）完成编译与部署");
            return 1;
        }

        var probeDirs = new List<string> { dataDir, modDeployDir, baseLibDir };
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (string dir in probeDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return ctx.LoadFromAssemblyPath(candidate);
                }
            }

            return null;
        };

        Assembly sts2 = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dataDir, "sts2.dll"));
        Assembly modAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modDll);
        Console.WriteLine($"game assembly: {sts2.GetName().Version}   mod: {mod} {modAssembly.GetName().Version}");

        // 1) 模组自己的类型
        foreach (string typeName in targets.Types)
        {
            Check(modAssembly.GetType(typeName) != null, "type " + typeName);
        }

        // 2) 补丁目标 + 实际挂载
        Type patchesClass = string.IsNullOrWhiteSpace(targets.PatchesClass)
            ? null
            : modAssembly.GetType(targets.PatchesClass);
        var harmony = new Harmony("verify." + mod.ToLowerInvariant());

        foreach (PatchTarget item in targets.Patches)
        {
            Type type = sts2.GetType(item.Type);
            if (type == null)
            {
                Fail($"type not found: {item.Type}");
                continue;
            }

            MethodInfo original = AccessTools.Method(type, item.Method, Type.EmptyTypes)
                                  ?? AccessTools.Method(type, item.Method);
            if (original == null)
            {
                Fail($"method not found: {item.Type}.{item.Method}");
                continue;
            }

            MethodInfo patch = patchesClass?.GetMethod(
                item.Patch, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (patch == null)
            {
                Fail($"patch method not found: {targets.PatchesClass}.{item.Patch}");
                continue;
            }

            try
            {
                bool isPrefix = string.Equals(item.Kind, "prefix", StringComparison.OrdinalIgnoreCase);
                if (isPrefix)
                {
                    harmony.Patch(original, new HarmonyMethod(patch), null);
                }
                else
                {
                    harmony.Patch(original, null, new HarmonyMethod(patch));
                }

                string kind = isPrefix ? " (prefix)" : "";
                Console.WriteLine($"  OK  {type.Name}.{original.Name}  <- {item.Patch}{kind}");
            }
            catch (Exception e)
            {
                Fail($"patch failed {type.Name}.{item.Method}: {e.Message}");
            }
        }

        // 3) 反射依赖的私有字段
        foreach (FieldTarget item in targets.Fields)
        {
            Type type = sts2.GetType(item.Type);
            Check(type != null && AccessTools.Field(type, item.Field) != null,
                $"field {item.Type}.{item.Field}");
        }

        // 4) 依赖的公开成员
        foreach (MemberTarget item in targets.Members)
        {
            Type type = sts2.GetType(item.Type);
            if (type == null)
            {
                Fail("type not found: " + item.Type);
                continue;
            }

            // 同名重载（例如 GenerateChecksum 有两个）用 GetMethod 会抛 AmbiguousMatch，
            // 这里只要求"至少存在一个同名成员"。
            bool found = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Any(m => m.Name == item.Member);
            Check(found, $"{type.Name}.{item.Member}");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static string Metadata(string key)
    {
        AssemblyMetadataAttribute attribute = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key);

        if (attribute == null || string.IsNullOrWhiteSpace(attribute.Value))
        {
            return null;
        }

        return attribute.Value.TrimEnd('\\', '/');
    }

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine("  OK  " + what);
        }
        else
        {
            Fail(what);
        }
    }

    private static void Fail(string what)
    {
        _failures++;
        Console.WriteLine("  FAIL " + what);
    }

    /// <summary>数据目录名里带平台标记的优先（同一台机器上装了多个平台导出时）。</summary>
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

    private sealed class TargetFile
    {
        public string Assembly { get; set; }
        public string PatchesClass { get; set; }
        public List<string> Types { get; set; } = new List<string>();
        public List<PatchTarget> Patches { get; set; } = new List<PatchTarget>();
        public List<FieldTarget> Fields { get; set; } = new List<FieldTarget>();
        public List<MemberTarget> Members { get; set; } = new List<MemberTarget>();
    }

    private sealed class PatchTarget
    {
        public string Type { get; set; }
        public string Method { get; set; }
        public string Patch { get; set; }
        public string Kind { get; set; }
    }

    private sealed class FieldTarget
    {
        public string Type { get; set; }
        public string Field { get; set; }
    }

    private sealed class MemberTarget
    {
        public string Type { get; set; }
        public string Member { get; set; }
    }
}
