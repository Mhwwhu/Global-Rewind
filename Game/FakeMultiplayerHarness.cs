using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using GlobalRewind.Logging;
using GlobalRewind.Players;

namespace GlobalRewind.Game;

/// <summary>
/// M0/M1 的测试脚手架：在一个进程里起一局 2~4 人的“假多人”。
///
/// 游戏本身就用这种做法测试（`IsSingleplayerOrFakeMultiplayer` 的注释写明是
/// bootstrapper 塞 dummy player）。这里全部走公开 API：
///   Player.CreateForNewRun + RunState.CreateForNewRun + SetUpNewSingleplayer +
///   FinalizeStartingRelics + Launch + NRun.Create + SetActInternal + EnterRoomDebug。
///
/// **本地玩家故意放在最后一个座位（1 号位）**，dummy 放 0 号位：
/// 任何残留的 `Players[0]` 都会立刻读错手牌/阶段，M0 的“去单人假设”就有了硬回归。
///
/// 只在 `GLOBAL_REWIND_TEST_HARNESS=1` 时启用；正式发布不暴露。
/// 它不测网络协议、不测 StateDivergence、真就绪语义也和真联机不同 ——
/// 只证明“状态并集 + 门禁 + Players[0] 修复”。
/// </summary>
internal static class FakeMultiplayerHarness
{
    private static bool _starting;
    private static HoldGameAction _hold;

    public static bool Enabled
    {
        get
        {
            try
            {
                return string.Equals(
                    Environment.GetEnvironmentVariable("GLOBAL_REWIND_TEST_HARNESS"),
                    "1", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Start(int playerCount)
    {
        if (!Enabled)
        {
            return "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game";
        }

        if (_starting)
        {
            return "already starting";
        }

        RunManager manager = RunManager.Instance;
        if (manager == null || NGame.Instance == null)
        {
            return "game is not ready";
        }

        if (manager.IsInProgress)
        {
            return "a run is already in progress; return to main menu first";
        }

        _starting = true;
        TaskHelper.RunSafely(StartAsync(Math.Clamp(playerCount, 2, 4)));
        return "fake multiplayer starting...";
    }

    private static async Task StartAsync(int playerCount)
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager == null || NGame.Instance == null)
            {
                return;
            }

            // MCP 在启动早期就能响应；必须等主菜单/资源初始化完成，否则 NRun.Create
            // 会在 NLogoAnimation 还没准备好的时候撞上空引用。
            await NGame.Instance.GameStartupComplete;

            var character = ModelDb.Character<Ironclad>();
            var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();

            // 最后一个座位是本地玩家（NetId=1），前面全是 dummy（NetId=2..N）。
            var players = new List<Player>(playerCount);
            for (int slot = 0; slot < playerCount - 1; slot++)
            {
                players.Add(Player.CreateForNewRun(character, unlockState, (ulong)(slot + 2)));
            }

            players.Add(Player.CreateForNewRun(character, unlockState, 1UL));

            var acts = ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList();
            RunState state = RunState.CreateForNewRun(
                players,
                acts,
                Array.Empty<ModifierModel>(),
                GameMode.Standard,
                isCustom: false,
                ascensionLevel: 0,
                seed: "GRTEST");

            manager.SetUpNewSingleplayer(state, shouldSave: false);
            // Bootstrapper 同款顺序：先 LoadRunAssets 把 AssetSets.RunSet 填好，
            // 否则后面的 SetActInternal → LoadActAssets 会在 SelectMany(null) 上炸。
            await PreloadManager.LoadRunAssets(state.Players.Select(player => player.Character));
            await manager.FinalizeStartingRelics();
            manager.Launch();

            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(state));
            await manager.SetActInternal(0);
            await manager.EnterRoomDebug(RoomType.Monster);

            ModLog.Info($"fake mp started: {playerCount} players, local net id 1 at slot {playerCount - 1}, "
                        + $"players=[{string.Join(",", PlayerScope.All.Select(p => p.NetId))}]");
        }
        catch (Exception e)
        {
            ModLog.Warn($"fake mp start failed: {e}");
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// 给指定玩家入队一个可控的 no-op 动作，让它停在队列里。
    /// 用来验证“有人在途 → 不抓点”；<see cref="Release"/> 放行后应恢复抓点。
    /// </summary>
    public static string Hold(ulong ownerNetId)
    {
        if (!Enabled)
        {
            return "disabled: set GLOBAL_REWIND_TEST_HARNESS=1";
        }

        RunManager manager = RunManager.Instance;
        if (manager == null || !manager.IsInProgress)
        {
            return "not in a run";
        }

        if (_hold != null)
        {
            return "already holding; release first";
        }

        ulong owner = ownerNetId != 0 ? ownerNetId : PlayerScope.LocalNetId;
        _hold = new HoldGameAction(owner);

        try
        {
            manager.ActionQueueSynchronizer.RequestEnqueue(_hold);
            return $"hold action enqueued for net id {owner}";
        }
        catch (Exception e)
        {
            _hold = null;
            return $"hold failed: {e.GetType().Name} {e.Message}";
        }
    }

    public static string Release()
    {
        HoldGameAction hold = _hold;
        if (hold == null)
        {
            return "no hold action";
        }

        _hold = null;
        hold.Release();
        return "hold released";
    }

    public static string Status()
    {
        return $"enabled={Enabled} starting={_starting} holding={_hold != null} "
               + $"players={PlayerScope.All.Count} local={PlayerScope.LocalNetId} "
               + $"net={PlayerScope.NetType}";
    }

    /// <summary>
    /// 只用于测试队列占用：ExecuteAction 一直等到 Release。
    /// 注意：`CombatReplayWriter` 会对 RecordableToReplay=false 的动作抛
    /// “Found unrecordable game action”，这是测试动作的已知副作用，不影响 mod 本体；
    /// 也正因为它在 ActionEnqueued 里先抛，`RewindRuntime` 的 depth 计数收不到这次入队，
    /// 静止门禁靠 ActionQueueSet.IsEmpty 兜住。
    /// </summary>
    private sealed class HoldGameAction : GameAction
    {
        private readonly TaskCompletionSource<bool> _release =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public HoldGameAction(ulong ownerId)
        {
            OwnerId = ownerId;
        }

        public override ulong OwnerId { get; }

        public override GameActionType ActionType => GameActionType.Any;

        public override bool RecordableToReplay => false;

        protected override async Task ExecuteAction()
        {
            await _release.Task;
        }

        public override INetAction ToNetAction()
        {
            // 假多人是 Singleplayer，动作不会走网络序列化；真联机测试不会用这个 harness。
            throw new NotSupportedException("test-only hold action is not networked");
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }
}
