using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.App;
using GlobalRewind.Logging;

namespace GlobalRewind.Patches;

/// <summary>
/// 所有 Harmony 补丁的唯一入口。
///
/// **合并**在这里：目标类型/方法、补丁方法、前缀还是后缀，全在同一张表里；
/// 补丁体也在本类。ModEntry 只调一次 <see cref="ApplyAll"/>，不再自己维护 Patch 逻辑。
///
/// 注意：不用 [HarmonyPatch] 特性扫描（实测 PatchAll 在这套装配上匹配到 0 个），
/// 必须显式 Harmony.Patch(...) 逐条挂，失败会写日志。
/// </summary>
internal static class ModPatches
{
    private readonly struct PatchSpec
    {
        public readonly Type Target;
        public readonly string Method;
        public readonly string PatchMethod;
        public readonly bool Prefix;

        public PatchSpec(Type target, string method, string patchMethod, bool prefix)
        {
            Target = target;
            Method = method;
            PatchMethod = patchMethod;
            Prefix = prefix;
        }
    }

    private static readonly PatchSpec[] Specs =
    {
        // 整局结束：清理内存与落盘
        new PatchSpec(typeof(RunManager), "CleanUp", nameof(OnRunCleanUp), prefix: true),

        // 游戏根节点的输入：热键事件入口之一（另外还有常驻 Node 与 ProcessFrame 轮询）
        new PatchSpec(typeof(NGame), "_Input", nameof(OnGameInput), prefix: false)
    };

    private const string HarmonyId = "GlobalRewind";

    public static void ApplyAll()
    {
        var harmony = new Harmony(HarmonyId);
        int applied = 0;
        foreach (PatchSpec spec in Specs)
        {
            if (Apply(harmony, spec))
            {
                applied++;
            }
        }

        ModLog.Info($"patches applied: {applied}/{Specs.Length}");
    }

    private static bool Apply(Harmony harmony, PatchSpec spec)
    {
        try
        {
            MethodInfo target = AccessTools.Method(spec.Target, spec.Method);
            if (target == null)
            {
                ModLog.Warn($"patch target missing: {spec.Target.Name}.{spec.Method}");
                return false;
            }

            MethodInfo patchMethod = typeof(ModPatches).GetMethod(
                spec.PatchMethod, BindingFlags.Public | BindingFlags.Static);
            if (patchMethod == null)
            {
                ModLog.Warn($"patch method missing: ModPatches.{spec.PatchMethod}");
                return false;
            }

            var harmonyMethod = new HarmonyMethod(patchMethod);
            harmony.Patch(target, spec.Prefix ? harmonyMethod : null, spec.Prefix ? null : harmonyMethod);
            ModLog.Info($"patched {spec.Target.Name}.{spec.Method} <- {spec.PatchMethod}");
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"patch failed {spec.Target.Name}.{spec.Method}: {e.GetType().Name} {e.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- 补丁体

    /// <summary>RunManager.CleanUp 前缀：整局结束，清空树与落盘。</summary>
    public static void OnRunCleanUp()
    {
        try
        {
            RewindRuntime.Instance.OnRunCleanUp();
        }
        catch (Exception e)
        {
            ModLog.Warn($"cleanup hook failed: {e.Message}");
        }
    }

    /// <summary>NGame._Input 后缀：把按键转给统一的输入分发器。</summary>
    public static void OnGameInput(InputEvent inputEvent)
    {
        RewindHotkeys.HandleEvent(inputEvent, "harmony");
    }
}
