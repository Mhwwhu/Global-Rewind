using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Logging;
using GlobalRewind.Players;

namespace GlobalRewind.Game;

/// <summary>
/// 恢复模型之后，把战斗画面重建到"新战斗"的状态。
///
/// 为什么整房间重建而不是逐个节点刷新：
///   · 旧房间释放时会自然解掉它挂在各节点上的事件订阅，不会留下指向已恢复状态的漏网回调；
///   · 手牌、血条、意图、能量、充能球都由游戏自己的初始化逻辑生成，比手工对字段可靠；
///   · 唯一需要特殊处理的是背景：部分 boss（Kaiser Crab）由背景节点绘制且被怪物模型缓存，
///     重建会留下指向已释放节点的引用，所以要把旧背景整体搬过来。
/// </summary>
internal static class CombatViewRebuilder
{
    private static FieldInfo _roomContainerField;

    public static bool Rebuild(out string error)
    {
        error = null;

        try
        {
            NRun run = NRun.Instance;
            if (run == null)
            {
                error = "没有正在进行的 NRun";
                return false;
            }

            IRunState state = RunManager.Instance?.DebugOnlyGetState();
            if (state?.CurrentRoom is not CombatRoom combatRoom)
            {
                error = "当前房间不是战斗房间";
                return false;
            }

            NCombatRoom oldRoom = run.CombatRoom;
            if (oldRoom == null)
            {
                // 没有战斗房间节点时不重建，否则会把当前场景（地图/事件）顶掉
                error = "当前没有战斗房间节点";
                return false;
            }

            // 1) 先把旧背景摘下来，避免随旧房间一起被 QueueFree
            Node carriedBackground = DetachBackground(oldRoom);

            // 2) 造新房间（此时还没进树，_Ready 未执行）
            NCombatRoom newRoom = NCombatRoom.Create(combatRoom, CombatRoomMode.ActiveCombat);
            if (newRoom == null)
            {
                error = "NCombatRoom.Create 返回 null";
                return false;
            }

            // 3) 换进场景容器；这一步会释放旧房间，但不会触发 ActiveScreenContext.Update
            if (!TrySetScene(run, newRoom, out error))
            {
                newRoom.QueueFree();
                return false;
            }

            // 4) 背景：优先复用旧的，没有再让房间自己建
            bool backgroundOk = TryAttachBackground(newRoom, carriedBackground);
            if (!backgroundOk && newRoom.Background == null)
            {
                newRoom.SetUpBackground(state);
            }

            // 5) 用恢复后的 CombatState 激活 UI（牌堆/能量/结束回合按钮）
            newRoom.Ui?.Activate(combatRoom.CombatState);

            // 6) 新建的房间只会给"之后进入手牌"的牌建节点：手牌节点是在抽牌/加牌时
            //    由 NPlayerHand.Add 建的，而恢复出来的手牌早就存在，不会触发任何事件。
            //    结果就是手牌区空白、点不了牌（游戏侧还会报 "card holder passed is null"）。
            //    这里按手牌顺序把它们逐个补进 Hand。
            SyncHand(newRoom, state);

            // 7) 药水腰带是常驻全局 UI，不在战斗房间里，房间重建不会动它。
            //    用过的药水在恢复后回到了模型里，但槽位还空着 —— 按模型对齐一次。
            SyncPotions(state);

            // 8) 敌人意图：意图图标只由 NCreature.UpdateIntent 创建，而它只在
            //    "准备下回合 / 力量值变化" 这些事件里被调用。新建的房间没人调，图标就是空的。
            SyncIntents(newRoom, combatRoom);

            // 9) 遗物计数器（手里剑/忍者镖/Pen Nib 之类）挂在顶栏，房间重建不会刷新，
            //    恢复后数值回到了模型里、标签还停在旧数字上。
            SyncRelicCounters(state);

            // 10) 充能球：槽位由 CombatSetUp 事件创建，房间重建同样错过，球会整组消失。
            SyncOrbs(newRoom, state);

            // 11) UI 有状态之后再更新屏幕上下文，否则 NCombatUi.Enable 会崩
            ActiveScreenContext.Instance.Update();

            // 12) 结束回合按钮放到最后：可见性与可交互性都由 TurnStarted / PlayerEndedTurn 事件驱动，
            //     新房间的按钮初始状态是 Hidden（停在屏幕外），没人发这些事件就永远不出现。
            //     必须在屏幕上下文更新之后做 —— RefreshEnabled 会按"当前屏幕是不是这个战斗房间"
            //     决定 Enable/Disable，早于它执行会被随后的一次刷新覆盖成不可点。
            SyncEndTurnButton(newRoom, combatRoom);

            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// 把恢复后的手牌补成可见的卡牌节点。
    /// 房间新建时手牌区是空的：卡牌节点只在抽牌/加牌时由 NPlayerHand.Add 创建，
    /// 而快照恢复出来的手牌不会触发这些事件。
    /// </summary>
    private static void SyncHand(NCombatRoom room, IRunState state)
    {
        try
        {
            NPlayerHand hand = room.Ui?.Hand;
            if (hand == null)
            {
                ModLog.Warn("hand sync skipped: NCombatUi.Hand 为空");
                return;
            }

            Player me = PlayerScope.Local;
            PlayerCombatState combatState = me?.PlayerCombatState;
            if (combatState == null)
            {
                return;
            }

            int added = 0;
            foreach (CardModel card in combatState.Hand.Cards)
            {
                if (card == null || hand.GetCard(card) != null)
                {
                    continue;
                }

                NCard node = NCard.Create(card);
                if (node == null)
                {
                    ModLog.Warn($"hand sync: NCard.Create 返回 null（{card.Id}）");
                    continue;
                }

                // index 省略即追加：按 Hand.Cards 的顺序逐个加，最终顺序与模型一致
                hand.Add(node);
                added++;
            }

            if (added > 0)
            {
                ModLog.Info($"hand rebuilt: {added} node(s), hand size {combatState.Hand.Cards.Count}");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"hand sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 按模型对齐药水腰带。腰带挂在常驻的全局 UI 上，房间重建不会刷新它，
    /// 于是"用过的药水在恢复后回到模型里、槽位却还空着"。
    /// </summary>
    private static void SyncPotions(IRunState state)
    {
        try
        {
            Player me = PlayerScope.Local;
            if (me == null)
            {
                return;
            }

            // 必须走 NRun.Instance 这一条：NGame 下可能还挂着上一局的旧 run 节点，
            // 按树搜索会先撞到它，把药水加进一个看不见的腰带里。
            NPotionContainer container = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
            container ??= FindPotionContainer(NGame.Instance);
            if (container == null)
            {
                ModLog.Warn("potion sync skipped: 找不到 NPotionContainer");
                return;
            }

            System.Collections.Generic.IReadOnlyList<PotionModel> slots = me.PotionSlots;
            int count = Math.Min(container.Holders.Count, slots.Count);
            int changed = 0;

            for (int i = 0; i < count; i++)
            {
                NPotionHolder holder = container.Holders[i];
                PotionModel want = slots[i];
                if (holder == null || ReferenceEquals(holder.Potion?.Model, want))
                {
                    continue;
                }

                ModLog.Info($"potion slot {i}: '{holder.Potion?.Model?.Id.Entry ?? "<empty>"}' -> '{want?.Id.Entry ?? "<empty>"}'");

                if (holder.HasPotion)
                {
                    holder.RemoveUsedPotion();
                }

                if (want != null)
                {
                    NPotion potion = NPotion.Create(want);
                    if (potion == null)
                    {
                        ModLog.Warn($"potion slot {i}: NPotion.Create 返回 null");
                        continue;
                    }

                    // 和 NPotionContainer.Add 一致：药水节点按 (-30,-30) 摆，才会落在 60x60 槽位正中
                    potion.Position = new Vector2(-30f, -30f);
                    holder.AddPotion(potion);
                }

                changed++;
            }

            if (changed > 0)
            {
                ModLog.Info($"potion belt synced: {changed} slot(s) on '{container.GetPath()}' ({container.Holders.Count} slots)");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"potion sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 按模型重建敌人头顶的意图图标。
    ///
    /// 意图图标不是 _Ready 里画的，而是 NCreature.UpdateIntent 按 Monster.NextMove.Intents 建的；
    /// 它只在"准备下回合"（PrepareForNextTurn）和力量值变化的回调里被调用。
    /// 房间重建出来的节点错过了这些事件，模型里的意图是对的、屏幕上却是空的。
    /// 这里直接按恢复后的模型补一次，并跳过 1 秒淡入，跳转后立刻可见。
    /// </summary>
    private static void SyncIntents(NCombatRoom room, CombatRoom combatRoom)
    {
        try
        {
            CombatState combat = combatRoom?.CombatState;
            if (combat?.Enemies == null || room == null)
            {
                return;
            }

            var targets = new System.Collections.Generic.List<Creature>();
            foreach (Player player in combat.Players)
            {
                if (player?.Creature != null)
                {
                    targets.Add(player.Creature);
                }
            }

            int shown = 0;
            foreach (Creature enemy in combat.Enemies)
            {
                if (enemy == null || !enemy.IsMonster || enemy.IsDead)
                {
                    continue;
                }

                NCreature node = room.GetCreatureNode(enemy);
                if (node?.IntentContainer == null)
                {
                    ModLog.Warn($"intent sync skipped: 找不到 {enemy.Name} 的 NCreature");
                    continue;
                }

                node.UpdateIntent(targets);
                node.IntentContainer.Modulate = Colors.White;
                shown++;
            }

            if (shown > 0)
            {
                ModLog.Info($"intents rebuilt: {shown} enemy node(s)");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"intent sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 让顶栏的遗物计数器重读模型。
    /// 计数器节点订阅的是 RelicModel.DisplayAmountChanged，恢复不会触发这个事件，
    /// 于是"模型回到 3、标签还写着 7"。这里把事件补发一次（只刷新，不改数值）。
    /// </summary>
    private static void SyncRelicCounters(IRunState state)
    {
        try
        {
            Player me = PlayerScope.Local;
            if (me == null)
            {
                return;
            }

            int refreshed = 0;
            foreach (RelicModel relic in me.Relics)
            {
                if (relic == null)
                {
                    continue;
                }

                MethodInfo notify = relic.GetType().GetMethod(
                    "InvokeDisplayAmountChanged",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (notify == null)
                {
                    continue;
                }

                notify.Invoke(relic, null);
                refreshed++;
            }

            if (refreshed > 0)
            {
                ModLog.Info($"relic counters refreshed: {refreshed}");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"relic counter sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 结束回合按钮。
    ///
    /// 按钮的可见性不是一个布尔量，而是内部状态机（Hidden / Enabled / Disabled）+ 屏幕外位置：
    /// 新房间的按钮 `_state` 初始就是 Hidden、停在 HidePos，只有 TurnStarted（回合开始）或
    /// PlayerUnendedTurn（撤销结束回合）会把它从屏幕外推回来。房间重建没人发这些事件，
    /// 于是"跳转后结束回合按钮不见了"。
    ///
    /// 这里直接走游戏自己的 OnTurnStarted：它会写回合号文案、按 PlayerCanTakeAction 判断能不能动
    /// （这一步天然把 PaelsEye 的额外回合算进去了），再 SetState(Enabled) 触发 AnimIn。
    ///
    /// 另外：如果跳到的节点正好是"已经按下结束回合"的状态，turn loop 不会自己继续，
    /// 必须把按钮显示成"撤销结束回合"并保持可点，否则这个节点是个死局。
    /// </summary>
    private static void SyncEndTurnButton(NCombatRoom room, CombatRoom combatRoom)
    {
        try
        {
            CombatState state = combatRoom?.CombatState;
            NEndTurnButton button = room?.Ui?.EndTurnButton;
            if (state == null || button == null)
            {
                return;
            }

            if (state.CurrentSide != CombatSide.Player || !CombatManager.Instance.IsInProgress)
            {
                SetButtonState(button, "Hidden");
                return;
            }

            MethodInfo onTurnStarted = typeof(NEndTurnButton).GetMethod(
                "OnTurnStarted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            onTurnStarted?.Invoke(button, new object[] { state });

            Player me = PlayerScope.Local;
            if (me != null && CombatManager.Instance.IsPlayerReadyToEndTurn(me))
            {
                SetButtonLabel(button, new LocString("gameplay_ui", "UNDO_END_TURN_BUTTON").GetFormattedText());
                SetButtonState(button, "Enabled");
                ModLog.Info("end turn button: undo mode (player already ended this turn)");
            }
            else
            {
                ModLog.Info("end turn button synced to current turn");
            }

            // SetState 只管"内部状态"，真正让按钮可点是 Enable()，而它由 RefreshEnabled 统一决定
            // （还要看当前屏幕上下文是不是这个战斗房间）。这里补一次，把状态落到可交互上。
            MethodInfo refresh = typeof(NEndTurnButton).GetMethod(
                "RefreshEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            refresh?.Invoke(button, null);
        }
        catch (Exception e)
        {
            ModLog.Warn($"end turn button sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static void SetButtonState(NEndTurnButton button, string stateName)
    {
        try
        {
            Type stateType = typeof(NEndTurnButton).GetNestedType("State", BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo setState = typeof(NEndTurnButton).GetMethod(
                "SetState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (stateType == null || setState == null)
            {
                ModLog.Warn("end turn button: SetState 不可用");
                return;
            }

            object value = Enum.Parse(stateType, stateName);
            setState.Invoke(button, new[] { value });
        }
        catch (Exception e)
        {
            ModLog.Warn($"end turn button: SetState({stateName}) failed: {e.Message}");
        }
    }

    private static void SetButtonLabel(NEndTurnButton button, string text)
    {
        try
        {
            FieldInfo labelField = typeof(NEndTurnButton).GetField(
                "_label", BindingFlags.Instance | BindingFlags.NonPublic);

            if (labelField?.GetValue(button) is not MegaLabel label)
            {
                return;
            }

            label.SetTextAutoSize(text);
        }
        catch (Exception e)
        {
            ModLog.Warn($"end turn button: label update failed: {e.Message}");
        }
    }

    /// <summary>
    /// 充能球。槽位只在 CombatSetUp 事件里创建（NOrbManager.OnCombatSetup），
    /// 房间重建错过这个事件 → 一个槽都没有，球全消失。
    /// 这里按 OrbQueue.Capacity 补槽、按 Orbs 顺序塞模型。
    /// </summary>
    private static void SyncOrbs(NCombatRoom room, IRunState state)
    {
        try
        {
            if (room == null || state == null)
            {
                return;
            }

            foreach (Player player in state.Players)
            {
                PlayerCombatState pcs = player?.PlayerCombatState;
                if (pcs == null || player.Creature == null)
                {
                    continue;
                }

                NCreature node = room.GetCreatureNode(player.Creature);
                NOrbManager manager = node?.OrbManager;
                if (manager == null)
                {
                    continue;
                }

                FieldInfo orbsField = typeof(NOrbManager).GetField(
                    "_orbs", BindingFlags.Instance | BindingFlags.NonPublic);
                if (orbsField?.GetValue(manager) is not List<NOrb> orbs)
                {
                    ModLog.Warn("orb sync skipped: 拿不到 NOrbManager._orbs");
                    continue;
                }

                int capacity = pcs.OrbQueue.Capacity;
                while (orbs.Count < capacity)
                {
                    manager.AddSlotAnim(1);
                }

                while (orbs.Count > capacity)
                {
                    manager.RemoveSlotAnim(1);
                }

                int filled = 0;
                for (int i = 0; i < capacity && i < orbs.Count; i++)
                {
                    OrbModel want = i < pcs.OrbQueue.Orbs.Count ? pcs.OrbQueue.Orbs[i] : null;
                    if (ReferenceEquals(orbs[i].Model, want))
                    {
                        continue;
                    }

                    orbs[i].ReplaceOrb(want);
                    filled++;
                }

                if (capacity > 0)
                {
                    ModLog.Info($"orbs synced: {filled} slot(s) changed, capacity {capacity}");
                }
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"orb sync failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static NPotionContainer FindPotionContainer(Node root)    {
        if (root == null)
        {
            return null;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is NPotionContainer container)
            {
                return container;
            }

            NPotionContainer found = FindPotionContainer(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool TrySetScene(NRun run, NCombatRoom room, out string error)
    {
        error = null;

        try
        {
            _roomContainerField ??= typeof(NRun).GetField(
                "_roomContainer", BindingFlags.Instance | BindingFlags.NonPublic);

            if (_roomContainerField?.GetValue(run) is not NSceneContainer container)
            {
                error = "找不到 NRun._roomContainer";
                return false;
            }

            container.SetCurrentScene(room);
            return true;
        }
        catch (Exception e)
        {
            error = $"SetCurrentScene 失败：{e.Message}";
            return false;
        }
    }

    private static Node DetachBackground(NCombatRoom room)
    {
        try
        {
            Node background = room?.Background;
            if (background == null || !GodotObject.IsInstanceValid(background))
            {
                return null;
            }

            Node parent = background.GetParent();
            parent?.RemoveChild(background);
            return background;
        }
        catch (Exception e)
        {
            ModLog.Warn($"detach background failed: {e.Message}");
            return null;
        }
    }

    private static bool TryAttachBackground(NCombatRoom room, Node background)
    {
        if (background == null || !GodotObject.IsInstanceValid(background))
        {
            return false;
        }

        try
        {
            PropertyInfo bgContainerProperty = typeof(NCombatRoom).GetProperty(
                "BgContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (bgContainerProperty?.GetValue(room) is not Control container)
            {
                background.QueueFree();
                return false;
            }

            container.AddChild(background);

            PropertyInfo backgroundProperty = typeof(NCombatRoom).GetProperty(
                "Background", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            backgroundProperty?.SetValue(room, background);
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"attach background failed: {e.Message}");
            background.QueueFree();
            return false;
        }
    }
}
