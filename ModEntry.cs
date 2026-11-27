using System;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.App;
using GlobalRewind.Logging;
using GlobalRewind.Mcp;
using GlobalRewind.Patches;
using GlobalRewind.Presentation;

namespace GlobalRewind;

/// <summary>
/// 组合根：只负责把各层接起来，不放业务逻辑。
///
/// 依赖方向：
///   ModEntry ──► Patches（合并后的 Harmony 补丁）
///            ──► App（RewindRuntime / RewindUiSource / 输入 / MCP）
///            ──► Presentation（RewindHud，只认 ITreeOverlaySource）
///
/// Presentation 不知道 App / Tree / Snapshot 的存在；数据通过
/// <see cref="RewindUiSource"/> 适配成 <see cref="TreeOverlaySnapshot"/> 注入。
/// </summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public static void Init()
    {
        try
        {
            RewindSettings.Instance.Load();

            RewindRuntime runtime = RewindRuntime.Instance;
            var uiSource = new RewindUiSource(runtime);
            RewindHud.Initialize(uiSource, () => NGame.Instance);
            RewindHud.HotkeysChanged += RewindHotkeys.SuppressUntilRelease;

            ModPatches.ApplyAll();

            CombatManager.Instance.CombatBegan += runtime.OnCombatBegan;
            CombatManager.Instance.TurnStarted += runtime.OnTurnStarted;
            CombatManager.Instance.CombatEnded += OnCombatFinished;
            CombatManager.Instance.CombatWon += OnCombatFinished;
            RunManager.Instance.RunStarted += OnRunStarted;

            string version = MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance?.ReleaseInfo?.Version ?? "unknown";
            ModLog.Info($"loaded on game {version}");

            // 输入通路：常驻 Node + ProcessFrame 轮询（外加 NGame._Input 的 Harmony 补丁）。
            RewindInputInstaller.Install();

            // 本模组自己的操作入口：内置轻量 MCP（默认 http://127.0.0.1:8766/mcp）。
            RewindMcpServer.Install();
        }
        catch (Exception e)
        {
            ModLog.Error($"init failed: {e}");
        }
    }

    private static void OnCombatFinished(MegaCrit.Sts2.Core.Rooms.CombatRoom room)
    {
        RewindRuntime.Instance.OnCombatEnded();
    }

    private static void OnRunStarted(MegaCrit.Sts2.Core.Runs.RunState state)
    {
        RewindRuntime.Instance.OnRunStarted();
    }
}
