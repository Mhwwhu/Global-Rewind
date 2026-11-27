# GlobalRewind 多人（战斗内）支持方案

> 目标版本：游戏 v0.114.0　基线：模组 v0.1.7
> 状态：**M0 + M1 + M2 已实现并通过双实例 ENet 实测**（v0.1.9，见 §12）；
> §1–§7 保留当初的方案与取舍原文，§11/§12 是落地记录。

## 0. 一句话

把“回溯点”从“我这台机器上的某一刻”升级成“所有玩家共同状态的一个静止切面”。

做法不是把每个人的快照拼起来，而是把同一份世界状态里的部分分成三类：

- **共享**：所有人、所有生物、所有回合流程 —— 每个端必须一模一样；
- **本地**：只影响本机视角的开关、UI、输入状态 —— 各端各自恢复；
- **传输/瞬时**：正在飞的消息、正在等的 Task、正在跑的动作 —— 不进快照，抓/跳前必须为空。

跳转从“本机写回”升级成“房主喊停 → 所有人一起写回 → 再互相对哈希”。

---

## 1. 现在为什么一进多人就停

| 现象 | 代码位置 | 原因 |
|---|---|---|
| 多人战斗完全不建树 | `RewindRuntime.OnCombatBegan` | `!IsSingleplayer` 直接 return |
| 本地玩家认错人 | `RewindRuntime.Context` / `IsStableDecisionPoint` / `GetCombatState`；`CombatViewRebuilder.SyncHand/SyncPotions/SyncRelicCounters/SyncOrbs` | 9 处 `Players[0]`，默认 0 号位就是本地玩家 |
| 稳定点判据是“本地”的 | `IsStableDecisionPoint` | `PlayerActionsDisabled` 只表示“我按了结束回合”，不表示所有人都结束了；`Players[0].Phase` 也不一定是本地玩家 |
| 动作结算计数会误判 | `_actionDepth` | 全局一个计数器；多人动作并行，A 结算完不代表 B 也结算完 |
| 校验和通道容易用错 | `CombatChecksum` | 现实现走的是 `GenerateChecksum(NetFullCombatState)` 的**纯哈希重载**，不会推进 `NextId`；但 `ChecksumTracker.GenerateChecksum(context, action)` 是“生成并记账”的另一条重载，多人下一旦误用就会凭空多出一次校验和，可能触发 `StateDivergence`。M0 把纯哈希入口显式化成 `ComputePure()`，并规定 MP 不回滚 `NextId` |
| 深拷贝指纹不能跨端比 | `ObjectGraphSnapshot.BuildCanonical` | 引用编码成 `#{recordIndex}`，图外对象编码成 `~类型#RuntimeHelpers.GetHashCode`，都是**本进程内**的标识 |
| 跳转只影响本机 | `RewindRuntime.JumpTo` | 没有通知对端的通道；一端写回、另一端不写回，立刻 desync |
| 玩家离开会留下幽灵 | `RewindNode.Snapshot` | memento 存的是 `Player` 实例引用；玩家退出后引用还在，恢复会“复活”一个不存在的人 |

---

## 2. 三种状态：共享 / 本地 / 传输

| 类别 | 具体内容 | 归属 | 策略 |
|---|---|---|---|
| **共享确定性状态** | 每个玩家的牌堆/手牌/能量/星/遗物/药水/球/箭矢/金币/玩家 RNG；所有生物 HP/格挡/Power；run RNG；回合数/阶段/当前侧；回合流程的就绪集合、额外回合名单；动作 id / hook id / choice id 计数器 | 所有玩家并集 | 所有端必须恢复到同一份；跨端只信游戏自己的 `NetFullCombatState` 纯哈希 + 切面 |
| **本地视图状态** | 本地玩家的 `CombatManager.PlayerActionsDisabled`、`ActionQueueSynchronizer.CombatState`、UI/相机/选中态 | 每端自己 | 各端恢复自己的；**不参与**跨端哈希 |
| **传输/瞬时状态** | `TaskCompletionSource`、`ReadyLock`、消息缓冲、在途动作队列、pending player choice、`ActionExecutor` 运行态、`ChecksumTracker.NextId` | 不存 | 抓/跳前必须为空；恢复后显式复位 |

**“并集”不是把每个人的快照拼起来。** 多人是确定性镜像：每个端本来就有一份包含所有人的 `RunState + CombatState + CombatTurnState`。要做的是把“属于所有人的字段”标成共享、把“只属于本机视角的字段”标成本地，而不是按人各拍一张照片再合并——那样引用关系会断。

一句人话：**共享的是桌上的牌，本地的是你坐的角度，传输的是还在空中飞的信。**

---

## 3. 切面（Frontier）：多人事件的“时间坐标”

多人不是一条时间线：A 在选牌、B 在出牌、C 的回合开始 hook 还在跑。所以节点不能只记“第几回合”。

```
Frontier {
  uint NextActionId;          // ActionQueueSet.NextActionId
  uint NextHookId;            // ActionQueueSynchronizer.NextHookId
  uint[] NextChoiceIds;       // PlayerChoiceSynchronizer.ChoiceIds，每个座位一个
  int RoundNumber;            // CombatState.RoundNumber
  CombatSide Side;            // CombatState.CurrentSide
  int[] TurnNumbers;          // 每个玩家的 TurnNumber
  PlayerTurnPhase[] Phases;   // 每个玩家的 Phase
  ulong[] ReadyToEndTurn;     // 已结束回合的玩家 NetId（排序）
  ulong[] ReadyToBeginEnemy;  // 已准备进入敌方回合的玩家
  ulong[] TakingExtraTurn;    // PaelsEye 之类额外回合的参与者
}
```

- **节点身份 = `Frontier + 共享纯哈希`**。不是时间戳，也不是“第几回合”。
- 两个状态“看起来一样”但 `NextActionId` 不同，就是不同状态。
- 第一版只在**所有活跃玩家都停在可操作状态、所有队列为空**时取切面（**静止切面**）。
- 以后做全局操作树时，把“静止切面”换成“一致切面”（每条玩家流各取一点，在途消息计入）即可，树/快照/UI 不用重写。这是最重要的扩展点。

**活跃玩家**：本回合真正要行动的人。额外回合名单非空时就是名单里的人，否则是全体存活玩家；死亡/未参与者视为已就绪，不阻塞切面。这和游戏 `StartTurn` 里“没在起始名单里的玩家自动 `SetReadyToEndTurn`”是同一套语义。

---

## 4. 松耦合的六个缝

| 缝 | 现在 | 以后 | 解决什么 |
|---|---|---|---|
| `PlayerScope` | 本地玩家 / 全体玩家 | 观战、回放、AI 托管 | 所有 `Players[0]` 走它，不再散落 |
| `SnapshotPart` + `FieldScope` | 共享 / 本地 / 排除三种作用域 | 新增 MP 专属字段只改表 | 快照核心不认识“多人” |
| `Frontier` + `ICutPolicy` | `QuiescentCutPolicy` | `ConsistentCutPolicy`（向量切面） | 异步事件不用重写树 |
| `IRewindTransition` / `TransitionGroup` | 一条边 = 一个动作（或一组并发动作） | 全局操作树的一条边 | 边可以带“谁、哪些 actionId/choiceId、从哪个切面到哪个切面” |
| `IRewindCoordinator` + `IRewindTransport` | Local / Host / Client，游戏 `INetMessage` | side-channel、replay 文件、跨进程 | 跳转流程不绑死网络实现 |
| `IRewindHistory` | 导出某节点之前的事件子集 | 中途加入补课、跨进程恢复、全局重放 | 不把“快照”当唯一恢复手段 |

---

## 5. 分阶段实施

### M0　去单人假设（不改行为）

- 新增 `PlayerScope`；替换 9 处 `Players[0]`（Runtime 5 + Rebuilder 4）。
- `IsStableDecisionPoint` 拆成 `LocalReadiness` / `GlobalReadiness`：SP 用前者，MP 用后者。
- `_actionDepth` 改成按 `OwnerId` 计数；`IsBusy` = 任一 owner 非零。
- 新增 `CombatChecksum.ComputePure()`：直接用 `GenerateChecksum(NetFullCombatState)` 的**纯哈希重载**，不推进 `NextId`、不发消息。所有 capture/jump/verify 统一走它；输入没变，SP 的校验和数值不变。
- `SnapshotPlan`：根按 part 分组；`FieldScope` 标共享/本地/排除；`ChecksumTracker.NextId` 在 MP 不加入快照。
- `CombatEventLog` 增加 `actor / stream / logical_id / frontier`。
- `RewindNode` 增加 `Frontier / Shared / Participants / State / Playable`。
- `tools/Verify` 增加新目标。

**验收**：SP 现有场景全部回归（PaelsEye、四类选牌、结束回合、跳转==重放）；MP 进战斗不再直接拒绝，只记录 `mp_detected`，不抓点。

### M1　多人只读：采集 + 验证（不跳）

- 新增 `QuiescenceProbe`，同时满足才允许抓点：
  - 所有活跃玩家 `Phase == Play`；
  - 所有活跃玩家 `!IsPlayerReadyToEndTurn`；
  - 本地 `!CombatManager.PlayerActionsDisabled`；
  - `ActionQueueSet.IsEmpty`；
  - `ActionExecutor.CurrentlyRunningAction == null && !ActionExecutor.IsPaused`；
  - `PlayerChoiceSynchronizer._receivedChoices` 为空；
  - `ActionQueueSynchronizer._hookActions` / `_requestedActionsWaitingForPlayerTurn` 为空；
  - `NextActionId` 连续 N 帧不变（防“刚好在两条动作之间”的假静止）。
- MP 只抓共享静止点；可选 `local` 诊断点（不可跳）。
- `rew_status` 加 `mp` 区块；`rew_state` 按玩家输出；新增 `rew_frontier`。
- 根节点/战斗开始：共享记录，但 `playable=false`（Phase 可能还是 Start，没有下一次 `TurnStarted`）；MP 不允许跳到它。

**验收**：2 人局双端 `rew_status.mp.quiescent` 同时为 true；`rew_capture` 双端 `frontier` 与纯哈希一致；`rew_verify` clean；不跳转。

### M2　协同跳转（所有人装同版本 mod，房主权威）

自定义 `INetMessage`（游戏原生支持 mod 消息子类型，见 `MessageTypes.Initialize` 里的 `ReflectionHelper.GetSubtypesInMods<INetMessage>()`）：

```
Host                              Client
 |-- GRHello(version, protocol) -->|   进战斗握手
 |-- GRAnchor(nodeId, frontier, checksum, label) -->|
 |<-- GRAnchorAck(ok, ownFrontier, ownChecksum) ----|
 |-- GRAnchorCommit(nodeId) ------>|   节点在双端都有 memento
 ...
 |-- GRFreeze(reqId, targetNode) ->|   禁输入 + 等本机空闲
 |<-- GRIdle(reqId, frontier) -----|
 |-- GRCommit(reqId, nodeId) ----->|   各自 Restore + Verify + 纯哈希
 |<-- GRRestored(reqId, ok, hash) -|
 |-- GRResume(reqId) ------------->|   重建画面 + 解冻
 |-- GRAbort(reqId, reason) ------>|   写回安全快照
```

规则：

- 节点 id 由房主分配；客户端只镜像，不能自增。
- `shared` 节点必须在所有参与者上都有 memento；缺一个就是 `partial`，不可跳。
- 跳转前每人先抓一份“冻结时的安全快照”；任一端失败/超时/哈希不一致 → 全员回滚，节点标 `unstable`。
- 恢复后显式复位：`ActionExecutor.Unpause()`、`ActionQueueSet.UnpauseAllPlayerQueues()`、清 `_receivedChoices`、清 `_hookActions` / `_requestedActionsWaitingForPlayerTurn`。
- `ChecksumTracker.NextId` **保持单调，不回滚**（见 §6.3）。
- `CombatViewRebuilder` 每端各自重建；只重建本地房间，其他玩家由 `NCombatRoom.Create` 一并重建。

**验收**：2 人局 A 出牌→B 出牌→A 跳回战前，双方血量/手牌/能量/回合/意图一致；双方 `rew_verify` clean；重放同一张牌 `action_id` 相同并 `capture_merged`；强制一端注入卡牌 → 哈希不一致 → 双端回滚、无人被踢。

### M3　异步操作树地基（不实现完整全局树）

- 事件按 `stream` 分列表；`rew_events` 支持 `actor` / `stream` / `logical_id` 过滤。
- `IRewindTransition` + `TransitionGroup`；节点边记录 `actors[] / actionIds[] / choiceIds[] / fromFrontier / toFrontier`。
- `ICutPolicy` 增加 `ConsistentCutPolicy` 的实验实现。
- `IRewindHistory` 导出某节点的“每人做了什么”偏序 JSON；先不执行。

**验收**：能回答“节点 N 之前 A/B 各做了什么、哪些是并发的”；导出脚本可被离线工具消费。

### M4　边界与体验

- 玩家离开/加入：roster 变化 → 受影响节点 `stale`，拒绝跳转；中途加入的人老节点只读。
- 权限：房主独跳 / 房主批准 / 任何人（配置）。
- UI：shared/local/partial/unstable 徽标；“等待其他玩家”遮罩；跳转进度。
- 验收：3 人局、一人中途退出、一人晚加入；跳转请求给出明确原因而不是卡死。

---

## 6. 关键取舍（必须知道）

### 6.1 所有人必须装同版本 mod

`GlobalRewind.json` 是 `affects_gameplay: true`，游戏握手会校验 gameplay mod 列表；而且消息类型 id 是**按类型名排序**生成的（`NetTypeCache` 用 `ContentSorter` 按 `Type.Name` 排序），少一个 mod 会让后续所有消息 id 错位。

这不是“建议”，是硬前提：**房主和所有客户端都必须装同一个版本的 GlobalRewind**。没装的人进不来，也不应该进。

### 6.2 跨端只信游戏自己的纯哈希

`DeepHash` 继续做“本进程恢复前后逐字节一致”的证明；跨端用 `NetFullCombatState` 纯哈希 + `Frontier`。不要试图把 `DeepHash` 当共享指纹——它编码了对象序号和运行时哈希，换一个进程就不一样。

### 6.3 不回滚 `ChecksumTracker.NextId`

回滚会让 checksum id 与对端错位，触发 `StateDivergence`；宁可少一个簿记维度，也不能踢人。新的 checksum 继续从当前值往上走，旧条目留在 `_checksums` 里也不会被命中（id 不复用）。

### 6.4 不回滚 TCS / 锁

`CombatTurnState` 只存数据字段（`PlayersReadyToEndTurn` / `PlayersThatCannotUnEndTurn` / `PlayersReadyToBeginEnemyTurn` / `PlayersTakingExtraTurn` / `PendingLoss` / `IsInProgress` / `IsStarting` / `IsEnemyTurnStarted` / `EndingPlayerTurnPhaseOne` / `EndingPlayerTurnPhaseTwo`），`EndTurnSignalSource` / `BeginEnemyTurnSignalSource` / `ReadyLock` 永远不写回。

恢复后靠“所有人重新按结束回合”驱动**现有的** turn loop：`SetReadyToEndTurn` 会去完成当前循环正在等的那个 TCS。这也是为什么共享节点必须是“所有活跃玩家都没结束回合”的状态。

### 6.5 不在非静止点抓/跳

有人在选牌、动作在途、队列非空时，快照既不可复现也不可回滚。宁可拒绝，不要“看起来恢复了”。

### 6.6 不跨端传 memento

memento 存的是运行时引用；跨端只能“各端各自有同一逻辑点的快照”或“重放”。前者是 M2，后者是 M3/M4 的 `IRewindHistory`。

### 6.7 回放录制

动作 id 会回退重用，官方 `CombatReplayWriter` 的录制可能错乱；MP 回溯期间应暂停/丢弃 replay 录制，或明确标记不支持。

---

## 7. 风险表

| 风险 | 后果 | 对策 |
|---|---|---|
| 各端快照不是同一逻辑点 | 跳转后立刻 desync / 踢人 | 只允许 `shared` 节点 + 全端 ACK + 纯哈希复核 + 失败回滚 |
| 用游戏 checksum 通道做诊断 | 伪造校验和、触发 `StateDivergence` | 只用 `GenerateChecksum(NetFullCombatState)` 纯哈希，禁止 `GenerateChecksum(context, action)` |
| 回滚 `ChecksumTracker.NextId` | checksum id 重用/误判 | MP 不回滚，保持单调 |
| 回滚 `_choiceIds` 但不清 `_receivedChoices` | 旧 choice id 命中过期 TCS | 恢复后清空并断言为空 |
| 写回 TCS / 锁 | turn loop 卡死 | 白名单只存数据；恢复后显式 Unpause |
| 玩家离开 | 幽灵 `Player` 引用 | roster guard；受影响节点 `stale` |
| 中途加入 | 没有旧节点 memento | 老节点只读；后续用 `IRewindHistory` 补课 |
| 2–4 人内存翻倍 | `MaxObjects=40000` 不够 / 卡顿 | 按人数提高上限 + 每节点内存预算 + 超限降级 `local-only` |
| 混合 mod 版本 | 消息 id 错位、握手失败 | 握手校验版本/协议号；不一致禁用 MP 回溯 |
| 官方 replay 录制 | 动作 id 重用导致 replay 混乱 | MP 回溯期间暂停录制或标记不支持 |

---

## 8. 验收清单

**SP 回归**

- [ ] 现有 8 项场景全过；`deep_equal=true`；`node_count` 不变。
- [ ] 事件流水字段完整（新增 `actor / stream / logical_id / frontier`）。
- [ ] `rew_checksum` 与旧值可对照（纯哈希输入相同，应同值）。

**MP 实机**

- [ ] 2 人静止点抓点，双端 `frontier` / 纯哈希一致。
- [ ] 协同跳转成功，双端 `rew_verify` clean，无人被踢。
- [ ] 一端状态被篡改 → 双端 abort + 回滚。
- [ ] 有人在选牌时请求跳转 → 等待/超时/拒绝，不硬跳。
- [ ] 玩家退出 → `stale` 节点拒绝。
- [ ] 中途加入 → 老节点只读。
- [ ] 3 人局不出现 2 人假设（`Players[0]` 全部清零）。

---

## 9. 一台机器上跑两个角色

### 9.1 真联机：游戏自带的 ENet 测试通道（最终验收用）

反编译确认（v0.114.0）：

- `--fastmp host_standard` → `NetHostGameService.StartENetHost(33771, 4)`，**不走 Steam 大厅**；
- `--fastmp join --clientId 1000` → `ENetClientConnectionInitializer(1000, "127.0.0.1", 33771)`；
- 游戏目录有 `steam_appid.txt`，可以直接启动 exe，不需要通过 Steam 客户端再开一份；
- `--fastmp` 会让 `StartHostAsync` 强制 `PlatformType.None`，所以两个实例在同一台机器上互连没问题。

启动方式（B 必须隔离用户目录和 MCP 端口）：

```bat
:: A：房主
"D:\3rd-party-software\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" --fastmp host_standard

:: B：客户端（独立 APPDATA，避免存档/日志/回溯落盘互相覆盖）
set APPDATA=D:\STS2Test\profile-b
set GLOBAL_REWIND_MCP_PORT=8767
"D:\3rd-party-software\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" --fastmp join --clientId 1000
```

操作：两边选角色 → Ready → 房主开始 → 进战斗。客户端窗口标题会变成 `Slay The Spire 2 (Client)`。

必须注意：

| 项 | 说明 |
|---|---|
| `APPDATA` 隔离 | 两个实例的存档、`godot.log`、`%APPDATA%\SlayTheSpire2\global_rewind` 不能共用；B 用独立目录 |
| `GLOBAL_REWIND_MCP_PORT` | A 默认 8766，B 设 8767；否则 B 的 MCP 绑定失败（日志会写 `mcp listen failed`） |
| 游戏侧 API 8080 | 只能给一个实例；另一个实例用 mod MCP + UI 操作，或后续给 STS2AIAgent 也做端口覆盖 |
| mod 版本 | 两个实例必须装同版本 GlobalRewind；游戏握手会校验 gameplay mod 列表 |
| Steam | `--fastmp` 的 host/join 都不依赖 Steam；Steam 初始化失败不影响 ENet 通道 |

### 9.2 假多人：一个进程两个角色（M0/M1 日常迭代用）

游戏本身有“fake multiplayer”概念（`IsSingleplayerOrFakeMultiplayer`，注释写明是给 bootstrapper 塞 dummy player 做测试的）。用公开 API 起一局 2 人单机：

```
players = [
    Player.CreateForNewRun(dummyCharacter, unlockState, netId: 2),   // 故意放 0 号位
    Player.CreateForNewRun(localCharacter, unlockState, netId: 1)    // 本地玩家放 1 号位
]
state = RunState.CreateForNewRun(players, ActModel.GetDefaultList(), [], Standard, false, 0, "GRTEST")
RunManager.SetUpNewSingleplayer(state, shouldSave: false)
await RunManager.FinalizeStartingRelics()
RunManager.Launch()
NGame.RootSceneContainer.SetCurrentScene(NRun.Create(state))
await RunManager.SetActInternal(0)
await RunManager.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter)
```

**故意把本地玩家放在 1 号位**：任何残留的 `Players[0]` 会立刻读错手牌/阶段，M0 的“去单人假设”就有了硬回归。

配套的 debug-only 工具（环境变量开关，不进正式发布）：

- `rew_fake_mp_start` / `rew_fake_mp_status`：起局、看两个玩家的 phase/ready/energy/frontier；
- `rew_fake_mp_hold <netId>`：给指定玩家入队一个可控的 no-op `GameAction`，让它停在队列里 → 验证“有人在途就不抓点”；
- `rew_fake_mp_release`：放行 → 验证“全部空闲就抓点”。

局限：`NetService.Type == Singleplayer`，不测网络协议、不测 `StateDivergence`、`AllPlayersReadyToEndTurn` 的语义也和真联机不同；UI 只显示本地手牌。它只证明“状态并集 + 门禁 + `Players[0]` 修复”，不能替代 9.1 的最终验收。

### 9.3 验收分工

| 测试 | 用哪条通道 |
|---|---|
| SP 现有场景回归 | 现有 `continue_run` 存档 |
| 2 人状态并集 / `Players[0]` / 门禁 | 9.2 假多人 |
| 真联机静止点 / 双端 frontier / 双端纯哈希 | 9.1 ENet 两进程 |
| M2 协同跳转协议（以后） | 9.1 ENet 两进程（必须） |

---

## 10. 决策记录（2026-11-26）

- **本轮范围**：M0 + M1 + M2（2026-11-27 追加 M2 并完成实测）；M3/M4 暂不做。
- **跳转权限**：v1 房主与客户端都能发起，实际都由房主统一执行（客户端只是发请求）。
- **抓点策略**：只抓“全体活跃玩家可操作 + 队列全空”的共享点；本地诊断点暂不做。
- **测试通道**：日常用 9.2 假多人；M1/M2 收尾用 9.1 ENet 两进程。

---

## 11. 实现状态（2026-11-26，M0 + M1 已落地）

### 已实现

- `Players/PlayerScope` + `TurnStateProbe`：本地玩家 / 全体玩家 / 本回合活跃玩家 / 假多人识别；
  `Players[0]` 的 9 处单人假设全部清掉（Runtime 5 + CombatViewRebuilder 4）。
- `RewindRuntime`：动作深度按 `OwnerId` 计数；`IsBusy` 额外看队列与执行器；
  多人动作 `AfterFinished` 后延迟一帧抓点（`PumpDeferredCapture`，由 `ProcessFrame` 驱动）。
- `SnapshotScope` / `SnapshotRoot` / `HashScope`：快照字段分共享/本地/排除；
  `SharedDeepHash` 只算共享字段（**本进程内**指纹，跨端仍只信 checksum）。
- `Frontier`：NextActionId / NextHookId / 每座位 choiceIds / Round / Side /
  每玩家 TurnNumber+Phase / 就绪集合 / 额外回合；节点合并判据 = checksum + Frontier + 共享字段指纹。
- `QuiescenceProbe`：所有活跃玩家 Phase=Play、未就绪、本地动作未禁用、队列空、
  执行器空闲、无 pending choice/hook/deferred、回合流程不在结束过渡。
- `RewindNode`：`Frontier / Shared / Playable / State / Participants / SharedObjectCount / SharedFieldCount`；
  多人根节点 `playable=false`（Phase 还是 None，跳过去打不出牌）。
- MCP：`rew_frontier`；`rew_status` 增加 `multiplayer / net_type / local_net_id / players[] / quiescent / frontier`；
  `rew_tree` / `rew_node` / `rew_capture` 增加切面与 shared/playable；`rew_events` 支持 `actor / stream` 过滤。
- 测试脚手架：`FakeMultiplayerHarness`（`GLOBAL_REWIND_TEST_HARNESS=1`）+ `rew_test_fake_mp / hold / release / status`；
  MCP 端口冲突自动向后找 10 个端口（双开时第二个实例自动落到 8767）。
- 真联机 M1：`IsMultiplayer`（Host/Client）下 `rew_jump` 明确拒绝，等待 M2 协同协议。

### 实测（v0.114.0）

- **假多人**（单进程 2 人，本地在 1 号位）：root `None/None` + turn start `Play/Play`；
  抓点合并；`rew_test_hold` 期间 `quiescent=false`、`rew_capture` 返回“还有动作在队列里没结算”；
  release 后恢复；`rew_jump` 到 turn start `verify=0 / deep_equal=true / shared verify=0`；
  根节点跳转被拒绝。
- **真联机**（`--fastmp host_standard` + `--fastmp join --clientId 1000`，ENet 33771）：
  双端 `frontier` 除 `local_net_id` 外完全一致；所有节点的 `checksum` 双端一致
  （root `8df35d06` / turn1 `cca53206` / card `7e99e692` / turn2 `3cc5a0b1`）；
  出牌后双端各建一个同名节点；结束回合后双端直接落到第 2 回合节点，无过渡节点；
  `rew_jump` 双端都返回“多人局 M1 只支持采集/验证”。
- 注意：`local_scope_hash`（原 `shared_deep_hash`）是**本进程内**的共享字段指纹，
  双端不一样是正常的；跨端只比 `checksum` + `frontier`。

### 已知边界

- M1 不做跨端协同跳转；真联机 `rew_jump` 拒绝。
- 中途加入/玩家离开的 roster guard 留到 M4；当前节点 `Participants` 已记录，但还没有 stale 判定。
- 多人动作的 `capture_skipped` 不逐帧写日志（避免刷屏）；`rew_status.last_quiescence_reason`
  会给出最近一次延迟抓点失败的原因。

---

## 12. 实现状态（2026-11-27，M2 已落地）

### 协议与文件

- `Net/RewindMessages.cs`：`GrHello / GrHelloAck / GrAnchor / GrAnchorAck / GrFreeze / GrIdle /
  GrCommit / GrRestored / GrResume / GrAbort / GrJumpRequest / GrCurrent`（12 个 `INetMessage`）。
  `Reliable`、`ShouldBuffer=false`；客户端回包 `ShouldBroadcast=false`。
  消息类型由游戏 `MessageTypes.Initialize()` 自动扫描 mod 程序集登记。
- `Net/RewindNetChannel.cs`：按当前 `NetService` 幂等注册/注销；房主广播、房主单发、客户端回房主。
- `Net/RewindCoordinator.cs`：房主权威状态机；网络回调只入队（锚点/冻结例外，见下），
  `ProcessFrame` 统一推进。
- `RewindTree.AddWithId`：客户端按房主编号镜像建点，`_nextId` 跟着外部编号走。
- `Snapshot/RestoreOutcome` + `RewindRuntime.RestoreNodeSnapshot`：SP 与 MP 共用同一套
  “写回 → Verify → DeepCompare → 纯哈希 → 失败回滚”逻辑。

### 关键实现细节（踩过的坑）

1. **冻结会把自己挡在探针外**：`BeginLocalFreeze` 先置 `_playerActionsDisabled=true`，
   而 `QuiescenceProbe` 原本要求它为 false，导致永远到不了静止点。
   现在探针支持 `ignoreLocalInputBlock`，只有协同冻结这条路径忽略它（结束回合过渡仍由
   `EndingPlayerTurnPhaseOne/Two` + 每玩家 ready 集合兜住）。
2. **锚点必须同步落地**：锚点在 `NetMessageBus` 回调里直接处理（早于本帧 `ProcessFrame`），
   否则客户端可能已经把紧随其后的动作执行了一半，checksum 对不上。
3. **镜像必须等本机静止**：动作收尾那一帧 checksum 已经等于锚点，但卡牌 `_currentTarget`、
   `_titleLocString`、`_baseStarCost` 等瞬时/惰性字段还没清；这时抓点会让 `rew_verify` 报不一致。
   现在非 force 锚点要求 `QuiescenceProbe` 通过；同时把这些卡牌字段从快照里排除。
4. **安全快照要留到 Resume 定论**：Commit 成功后提前把 safety 置空，会导致“对端失败、房主
   无法回滚”（实测 checksum 停在目标节点、树却显示当前节点）。现在 success/abort 之后才释放。
5. **重放合并不发锚点**：房主 `capture_merged` 时用 `GrCurrentMessage` 同步当前指针，
   否则客户端 current_id 会永远停在旧节点。
6. **Idle 阶段核对的是“当前节点”**：各端此刻还没写回，基准必须是 `Tree.Current` 的 checksum/frontier，
   不是目标节点的。

### 实测（v0.114.0，双实例 ENet 33771）

| 场景 | 结果 |
|---|---|
| 双端节点一致性 | 4 个节点 id / checksum / `shared` 全同；`frontier` 除 `local_net_id` 外一致 |
| 房主发起跳转 | `pending=true` → `ok=true, phase=resumed`；双端 `rew_verify` 0 不一致 |
| 跨回合跳转（第 2 → 第 1 回合） | 成功；再结束回合，双端落到同一个第 2 回合节点（checksum 相同） |
| 客户端发起跳转 | 请求转发给房主执行，双端 `rew_verify` 0 不一致 |
| 一方已结束回合时跳转 | 等待 ~8s 后 `aborted`，原因「本端未能到达静止点：1000:IRONCLAD 已结束回合」 |
| 客户端写回故障（`rew_test_poison_restore`） | 客户端 `mp_restored ok=false` → 房主 `mp_abort rollback=True` → `mp_rollback`；双端回到跳转前 checksum，verify 仍为 0 |
| 重放同一张牌 | 房主 `capture_merged` 回原节点；`GrCurrentMessage` 让客户端 current 跟上 |

### 多人剪枝（v0.1.10）

- 只有房主能发起；客户端 `rew_prune` 直接拒绝。
- 协议：`GrPrunePrepare → GrPruneReady(ok, current, nodes, treeHash) → GrPruneCommit →
  GrPruneDone(nodes, treeHash)`；任一端拒绝 / 树指纹不一致 / 超时 → `GrPruneAbort`，所有人都不动树。
- 树指纹 = `current | nextId | (id:parent:checksum)…` 的 FNV-1a 64；准备阶段必须全端一致，
  提交后再核一次（`mp_prune_diverged` 告警）。
- 安全状态：`QuiescenceProbe` + 队列为空 + 无待确认锚点 + 无跳转/剪枝会话；
  准备到提交之间挡输入、暂停队列、禁止抓点，完成后立刻恢复。
- 实测：节点 3/4 为兄弟、当前在 4，房主剪 3 → 双端只剩 1/2/4、`current=4`、`rew_verify` 0 不一致、
  双端 `nodes/3.json` 都删除；非静止点、根节点、当前路径三种拒绝都给出明确原因。

### 动作逻辑边界抓点（v0.1.11）

- **问题**：抓点只看“整条队列静止”，快速连点时下一个动作早已入队，`SettleAction` 的
  `HasTrackedActions` 直接返回、`OnActionEnqueued` 又清掉 pending，最后只剩最后一个动作的节点。
- **修法**：把抓点绑到动作的逻辑边界——`Execute` 已结束、`CurrentlyRunningAction` 还是它、
  下一个动作尚未开始。`QuiescenceProbe.TryCaptureBoundary` 与静止点的区别只有两条：
  允许队列非空、允许当前动作是刚结算的这个；其余（玩家回合、全员 Play、未就绪、无 hook/choice、
  输入未禁用）一条不放松。
- 派生 hook 未结算完时等最后一个 hook；纯内部/怪物动作不建点；边界不成立才退回延迟抓点。
- **合并**：新增 `HashScope.Merge`（`Full` 去掉 `ActionQueueSet._nextId`），用于兄弟节点合并。
  排队中的动作在入队时就占了一个编号，模型状态相同但 `DeepHash` 不同，合并哈希能避免重复节点。
- **客户端镜像**：锚点到达时本端可能已在执行下一个动作，所以客户端在自己的动作边界先抓一份
  镜像快照放进有界缓存（6 条 / 10s），锚点按 `checksum + frontier` 命中；未命中才实时抓。
- 实测：真联机房主连发 2 张 → 双端 `#3/#4` 节点完全一致，客户端日志
  `mp_mirror_cached → mp_anchor_cache_hit → mirror`；跳到中间节点双端 `verify=0`。
- 已知限制：结束回合 UI 在卡牌结算瞬间读取手牌费用会惰性创建 `CardEnergyCost`，
  导致“重放同一张牌”的对象图多出这些缓存对象、合并失败（多一个内容相同的兄弟节点）。
  节点本身有效；后续可把惰性缓存按“默认值等价于 null”纳入合并哈希。

### 仍未做（M3/M4）

- 全局异步操作树（`IRewindTransition` / `ConsistentCutPolicy` / `IRewindHistory`）。
- roster 变化（玩家离开/中途加入）的 `stale` 判定与只读节点。
- 跳转/剪枝权限配置（当前剪枝固定房主；跳转客户端可发起、房主执行）。
- 官方 replay 录制在 MP 回溯期间的暂停/标记（当前未处理）。


