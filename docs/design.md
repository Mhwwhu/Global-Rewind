# Global Rewind 设计说明

> 对应代码：本仓库根目录　目标版本：v0.114.0（v0.113.1 起可用）
> 本文只描述**代码实际做到的事**，以及明确还没做的事。
> v0.1.7：树形 UI 重做（圆点节点 / 连线折叠 / 缩放 / 拖拽 / 可调透明度 / 可改键）。
> v0.1.6：稳定决策点门禁（结束回合过渡态不建点）+ 全事件记录 + 官方 replay 确定性计数器入快照 + 跳转与重放逐字节等价。
> v0.1.11：抓点绑定动作逻辑边界（不再要求队列全空），快速连点逐次建点；合并哈希忽略已分配动作编号。

## 1. 目标与非目标

**目标**

- 单场战斗内（普通战斗、事件战斗）保存任意多个状态点，可回溯、可任意跳转。
- 保留整棵探索树：从旧节点继续操作产生新分支，不覆盖旧分支。
- 编号全局单调递增；可手动剪掉某个节点及其子树。
- 圆点节点 + 连线折叠的树形可视化；点击跳转、右键剪枝、点击连线折叠子树。
- 树与节点摘要落盘；整局结束时清理。
- **任何情况下跳转到某节点，完全等价于「从战斗开始按操作树重放到该节点」**：状态相关对象之间的深拷贝序列化结果逐字节一致。

**非目标（v0.1）**

- 不做操作回放来恢复状态（恢复靠原地 memento 写回模型）。但**复用官方 replay 的事件入口与确定性计数器**，
  使「跳转」与「重放」在编号、RNG、流程状态上不可区分。
- 多人 M2（协同跳转）已实现：房主分配节点 id、客户端镜像锚点，
  `Freeze/Idle/Commit/Restored/Resume/Abort` 全部走游戏原生 `INetMessage`；
  协议与实测见 [multiplayer-plan.md](multiplayer-plan.md)。
  状态集覆盖全体玩家并集，节点身份 = `Frontier` 切面 + `NetFullCombatState` 纯哈希。
- 不做跨进程恢复（见 §6）。
- 不在敌人回合中途跳转（要求动作队列空闲）。

## 2. 为什么是"原地 memento"

三条路线里，回放重放成本随回合线性增长且事件战斗无法重建房间栈；深拷贝快照要自己维护克隆语义，容易漏共享引用。
原地 memento 的做法是：**遍历对象图，记下每个字段，恢复时写回同一批实例**。

好处：

- 不会漏字段（新增字段自动被覆盖），不需要维护字段清单。
- 恢复后引用关系、UI 节点上的事件订阅、模型之间的互相指向都保持有效。
- 被撤销掉的卡牌/生物对象靠快照的强引用存活，能被原样写回。

代价：内存占用大，必须限制快照数量或做序列化压缩。

## 3. 快照边界

### 3.1 根对象

```
RunManager.DebugOnlyGetState()                    // RunState：玩家、牌组、遗物、药水、RNG、地图、当前房间
CombatManager.History                             // 战斗历史（很多卡查"本回合是否打过攻击"）
CombatManager.StateTracker                        // 状态变更追踪器（含订阅关系，委托本身不采集）
CombatManager._turnState (CombatTurnState)        // 回合流程：结束回合就绪集合、额外回合名单、阶段标志
Player.Creature.CombatState (CombatState)         // 生物、双方阵容、回合数、当前行动方

RunManager.ActionQueueSet                         // _nextId        —— 官方 replay 的 nextActionId
RunManager.ActionQueueSynchronizer                // _nextHookId     —— 官方 replay 的 nextHookId
RunManager.PlayerChoiceSynchronizer               // _choiceIds      —— 官方 replay 的 choiceIds
RunManager.ChecksumTracker                        // NextId          —— 官方 replay 的 nextChecksumId
CombatManager                                     // _playerActionsDisabled —— 牌/药水能不能点
```

后五个是 v0.1.5 加进来的。依据来自 `CombatReplayWriter.RecordInitialState`：它把
`choiceIds / nextActionId / nextHookId / nextChecksumId / serializableRun` 作为战斗起点记录下来，
后续每个动作、hook、玩家选择、校验和的编号都从它们派生。

**不恢复这些计数器，"跳转到节点"就不是"从战斗开始重放到该节点"**：跳回旧节点后新动作的 id
会接着当前值继续递增，而不是复用重放时那个 id。`rew_events` 里可以看到这一点。

`CombatState` 其实可以从 `RunState` 走到，单独作为根只是保证它一定被覆盖。

M0/M1 起根带作用域（`SnapshotRoot` / `SnapshotScope`）：
`RunState / History / StateTracker / CombatTurnState / CombatState / ActionQueueSet /
ActionQueueSynchronizer / PlayerChoiceSynchronizer` 是 **Shared**；
`CombatManager` 是 **Local**（`_playerActionsDisabled` 只影响本机视角，不参与跨端哈希）；
`ActionQueueSynchronizer.CombatState` 也是 Local 字段。
**多人局不加入 `ChecksumTracker` 根**：`NextId` 回滚会让 checksum id 与对端错位，触发 `StateDivergence`。

### 3.2 按类型的字段白名单（`MementoRules.RestrictedFields`）

上面那批同步器 / 管理器**内部有大量不该进快照的字段**：`_netService`、`_messageBuffer`、
事件委托、`TaskCompletionSource`、`ReadyLock`、`CancellationTokenSource`。
所以它们不是「整体可遍历」，而是命中字段白名单：

| 类型 | 允许进快照的字段 |
|---|---|
| `CombatTurnState` | `PlayersReadyToEndTurn` / `PlayersThatCannotUnEndTurn` / `PlayersReadyToBeginEnemyTurn` / `PlayersTakingExtraTurn` / `PendingLoss` / `IsInProgress` / `IsStarting` / `IsEnemyTurnStarted` / `EndingPlayerTurnPhaseOne` / `EndingPlayerTurnPhaseTwo` |
| `ActionQueueSet` | `_nextId` |
| `ActionQueueSynchronizer` | `_nextHookId` |
| `PlayerChoiceSynchronizer` | `_choiceIds` |
| `ChecksumTracker` | `NextId`（自动属性 backing field） |
| `CombatManager` | `_playerActionsDisabled` |

`CombatTurnState` 在 v0.1.3 之前是整体排除的（怕写回 `ReadyLock` / TCS 打断 turn loop），
后来发现「结束回合按钮消失」「PaelsEye 额外回合」都源于流程状态没恢复，于是改成白名单：
只存数据，不碰任何锁与等待源。

`NormalizeAfterRestore()` **不会**再按 `IsPlayerReadyToEndTurn` 重算 `_playerActionsDisabled`。
重算会让运行态偏离节点，直接破坏逐字节等价；真值就是快照里捕获的那一刻。

### 3.3 明确排除

| 排除项 | 原因 |
|---|---|
| `GodotObject` 及 `Godot.*` 类型 | 引擎对象生命周期不归模组管，写回旧节点必崩 |
| 委托 / 事件 | 快照不负责重连订阅；重建房间时由节点自行订阅 |
| `Task` / `CancellationTokenSource` / 线程原语 | 状态机不属于战斗状态 |
| 反射元数据（`Type` / `MemberInfo`） | 不可变 |
| logger | 无状态 |
| canonical（不可变）模型 | 共享内容，只存引用不深入，否则会把整个内容库拖进快照 |
| 药水的 `IsQueued` | 入队时置位、结算后游戏自己清；存进去会让恢复后的即时状态与捕获时对不上 |

### 3.4 集合的处理

引用字段只记引用是不够的——`List<Creature>` 被清空再填充时，引用没变但内容变了。所以对集合额外存一份元素序列：

- 数组：拷贝元素，恢复时 `Array.SetValue` 写回原数组；
- `IList`：`Clear()` + 逐个 `Add`（保持顺序，这就是抽牌堆顺序）；
- `IDictionary`：`Clear()` + 按原键值写回；
- `HashSet<T>` 一类：反射调用 `Clear()` / `Add(T)`。

`readonly` 字段（大量 `private readonly List<T> _x`）只恢复内容、不写引用，避免触发运行时的 initonly 限制。
`PlayerChoiceSynchronizer._choiceIds` 正是这种情况：`WriteReference=false`，只 `RestoreContents()`。

单集合元素上限 65536，对象总数上限 40000，超出即停止扩展并把 `Truncated` 置位，防止异常局把内存打满。

## 4. 节点与树

### 4.1 建点时机与事件入口

只有"稳定决策点"会建点。三个建点时机：

1. `CombatManager.CombatBegan` → 根节点"战斗开始"（`force`，唯一例外）；
2. `CombatManager.TurnStarted` 且 `CurrentSide == Player` → "第 N 回合"；
3. **玩家动作的逻辑边界**（v0.1.11）：`GameAction.Execute` 已结束、`ActionExecutor.CurrentlyRunningAction`
   还是这个动作、下一个动作尚未开始 → 以该动作命名。

第 3 条不要求动作队列为空：快速连点时下一个动作早已入队，但它属于"未来"，不参与当前快照。
动作派生出的 hook 还没结算完时，等最后一个 hook 结算再抓（标签仍用父动作）；
怪物动作、`ReadyToBeginEnemyTurnAction` 不建点；边界判据不成立时才退回"等下一个静止点"的延迟抓点。
动画只参与跳转后的画面重建，不参与抓点判定。

#### 稳定决策点门禁（v0.1.6 新增）

`RewindRuntime.IsStableDecisionPoint()` 用游戏自己的语义判定，三个条件同时成立：

| 判据 | 依据 |
|---|---|
| `CombatState.CurrentSide == Player` | 敌方回合的 hook / 结算动作不该成为分支点 |
| `!CombatManager.PlayerActionsDisabled` | `OnEndedTurnLocally()` 在按下结束回合按钮时立刻置 `true`，直到下一次 `TurnStarted` 前才清成 `false`（单人局也用它防止 `AfterTurnStart` hook 跑完前打牌）——官方定义的"过渡中" |
| `PlayerCombatState.Phase == PlayerTurnPhase.Play` | 官方定义 `Play` 为"允许玩家手动打牌和使用药水"的阶段；`Start` / `AutoPrePlay` / `AutoPostPlay` / `End` / `None` 都是过渡。`RunAutoPrePlayPhase` 在 `TurnStarted?.Invoke` 之前已把 `Phase` 设成 `Play` |

**为什么必须加**：单人局点结束回合后，`EndPlayerTurnAction` / `ReadyToBeginEnemyTurnAction`
完成时动作队列是瞬时空的、`CurrentSide` 也还是 `Player`，v0.1.5 只检查 side 会在这里建点，
得到"按钮处于撤销态、牌不能打"的中间过渡状态。实机的树会是
`#2 第1回合 → #3 结束回合 → #4 ReadyToBeginEnemyTurnAction → #5 第2回合`。
正常游玩中这些只是一闪而过的过渡，点击结束回合应该**直接进入下一回合**。

现在这些 `SettleAction` 只写一条 `capture_skipped`（带具体原因，如"玩家动作被禁用
（结束回合过渡中）"），不建节点；事件记录（动作入队 / 完成 / 校验和 / 回合切换）一条不少。
带 PaelsEye 的额外回合里消耗手牌的 hook 链同样被门禁覆盖。
`rew_status` 暴露 `stable` 与 `last_capture_skip_reason`，`rew_capture` 在过渡期返回
`skipped_transition=true` 和原因。

v0.1.5 起**没有动作白名单**。事件入口下沉到官方 replay 用的同一个统一入队点：

| 入口 | 覆盖 |
|---|---|
| `ActionQueueSet.ActionEnqueued` | 全部 `GameAction`（含 `GenericHookGameAction`）——单人局 `RequestEnqueue` 与 `RequestEnqueueHookAction` 最终都落到 `EnqueueWithoutSynchronizing` |
| `GameAction.AfterFinished` / `BeforeCancelled` | 动作完成 / 取消（官方自己也是两条一起挂来清理订阅） |
| `ActionQueueSet.ActionResumed` | 玩家选择后动作恢复 |
| `PlayerChoiceSynchronizer.PlayerChoiceReceived` | 消耗 / 变牌 / 弃牌 / 保留手牌等选牌类操作（`PlayerChoiceType` = `CombatCard` / `MutableCard` / `CanonicalCard` / `DeckCard` / `Player` / `Index`），这类操作**不产生新的 GameAction** |
| `ChecksumTracker.ChecksumGenerated` | 每个动作结算后的校验和生成 |

订阅在 `RunStarted` / `CombatBegan` 幂等执行（按同步器实例引用比对），`RunCleanUp` 时退订。
`_pendingLabels.Remove()` 做幂等，避免同一次动作被完成/取消两条回调各减一次 depth。

**合并判据是 `checksum + DeepHash`**，不是只看校验和：按下结束回合时 `NetFullCombatState` 的校验和
可能不变，只有「谁已就绪」变了，只看校验和会把两个语义不同的状态合并掉。
同一父节点下 checksum 与深拷贝指纹都相同的兄弟节点直接复用（`FindTwin`）。
跨父节点的同态状态目前不合并（`FindTwin` 只扫直接子节点）；改成全局合并会让图变成 DAG，
可视化方案见 [dag-visualization.md](dag-visualization.md)（设计稿，未实现）。

动作执行中（`busy`）抓点与跳转都会被拒绝——半应用的状态既不可复现也没意义。

### 4.2 分支与编号

节点 id 从 1 单调递增，`NextId` 只增不减。当前节点被设为新节点；跳回旧节点后继续操作，
新节点挂在旧节点下面，于是形成分支。剪枝只删节点，不回收编号；
`RewindTree.Prune` 输出 `removedIds`，`RewindStore.DeleteNode` 同步删掉 `nodes/N.json`，不留孤儿文件。

### 4.3 树形布局

布局由 `Presentation/TreeLayout.cs` 纯计算，不涉及 Godot 节点：

- 第一个子节点在父节点**正下方**（X 相同）；
- 其余子节点与它同高，从左往右排；第 i 个放在「第 i-1 个子节点子树最右侧节点」右边，
  间距 = 节点直径 + 固定间隙 → 任意两棵子树不重叠且尽量贴近；
- 被折叠的节点在布局中等同于**叶子**（UI 上的空心圆），因此折叠/展开会重排整棵树；
- 缩放 = 把自然坐标乘系数后重建，不用 `Control.Scale`（否则 ScrollContainer 尺寸与命中区域会错位）。

折叠状态是 UI 层的 `HashSet<int>`，不进 `RewindTree`；节点的折叠标记与树结构解耦，
所以嵌套折叠在展开外层时会原样保留。当前节点变化时自动展开它的祖先（保证"当前"可见），
但用户手动折叠时不强制展开。

连线用「父节点竖干 + 同高横排 + 竖向落点」：第一个子节点的竖线就是父节点的竖干延伸，
其余子节点的边是「横段 + 竖段」，两段都可点击（命中区域比线宽大，便于点中）。

### 4.4 分层与 UI 解耦

```
ModEntry（组合根）
  ├─► Patches/ModPatches        合并后的补丁表 + 补丁体
  ├─► App/RewindRuntime         领域逻辑（快照/跳转/落盘），不认识 UI
  ├─► App/RewindUiSource        实现 ITreeOverlaySource：逻辑 → UI 的唯一翻译层
  ├─► App/RewindHotkeys/Input   输入与热键分发
  ├─► Presentation/*            纯 UI：契约、布局、Godot 控件
  ├─► Game/CombatViewRebuilder  游戏房间的 UI 重建（游戏集成）
  └─► Mcp/RewindMcpServer       控制面
```

**Presentation 的边界**：
- 只依赖 Godot + `ITreeOverlaySource` / `TreeOverlaySnapshot` / `TreeNodeView` + UI 设置；
- 不 `using` 任何 `GlobalRewind.App` / `GlobalRewind.Tree` / `GlobalRewind.Snapshot` / `MegaCrit.*`；
- 宿主节点由 `Initialize(source, hostProvider)` 注入，UI 不自己找 `NGame`；
- 只发无语义事件：`NodeActivated` / `NodeContextActivated` / `HotkeysChanged`。
  "左键=跳转、右键=剪枝" 的语义全部在 `App/RewindUiSource`。
- 给一份任意 `TreeOverlaySnapshot` + 一个宿主 Node，UI 就能独立跑（不依赖战斗/回溯）。

**逻辑层的边界**：
- `RewindRuntime` 只发 `Changed` / `SessionEnded` 事件，不引用任何 UI 类型；
- 数据到 UI 的翻译（标题、副标题、tooltip、空状态文案、节点计数）全在 `RewindUiSource`；
- 补丁只剩 `Patches/ModPatches.cs` 一个入口：目标表、前缀/后缀、补丁体同处一文件。

## 5. 恢复流程

```
guard: 战斗中 && _actionDepth == 0 && 节点有内存快照
  ↓
抓一份 live 安全快照（失败也继续）
  ↓
node.Snapshot.Restore()          // 写回同一批实例（逆序：先子后父，结构体才能落进字段）
  ↓
node.Snapshot.Verify()           // 逐字段回读：引用/标量/集合内容与顺序
  ↓
node.Snapshot.DeepCompare()      // 规范序列化（捕获态 vs 活态）逐字节比较；不一致直接拒绝并回滚
  ↓
NormalizeAfterRestore()          // 只清药水 IsQueued；不重算任何管理器标志
  ↓
CombatChecksum.TryCompute()      // NetFullCombatState 的 XxHash32，与节点记录比对
  ↓
  ├─ 全通过 → 切当前节点 → 重建战斗画面
  └─ 任一失败 → 写回安全快照，报错，当前节点不变
```

`DeepCompare()` 把「快照里记的」和「活对象重新读出来的」各做一次固定顺序的规范序列化再比：
值编码含 `b/i/f/d/s/e/#/~`，浮点按 bit（`SingleToInt32Bits` / `DoubleToInt64Bits`），
结构体按字段内容递归编码，集合按编码排序或保留顺序。取 SHA256 前 16 字节作为 `DeepHash`。
不一致时返回 diff 并**拒绝跳转**，而不是"看起来恢复了但状态其实偏了"。

### 5.1 画面重建

整房间重建，而不是逐节点打补丁：

1. 先把旧房间的背景节点摘下来（Kaiser Crab 这类 boss 由背景绘制，怪物模型还缓存着它）；
2. `NCombatRoom.Create(combatRoom, ActiveCombat)` 造新房间；
3. 通过 `NRun._roomContainer.SetCurrentScene` 换入（**不**调用 `NRun.SetCurrentRoom`，它会立刻
   `ActiveScreenContext.Update()`，在 UI 激活前触发 `NCombatUi.Enable` 会崩）；
4. 背景复用旧的，没有则 `SetUpBackground`；
5. `newRoom.Ui.Activate(combatRoom.CombatState)` 重建牌堆、能量、结束回合按钮；
6. `SyncHand` 补手牌节点（手牌节点只在抽牌/加牌时创建，恢复出来的手牌不触发事件）；
7. `SyncPotions` 对齐常驻顶栏的药水腰带（节点按 `(-30,-30)` 摆才落在槽位正中）；
8. `SyncIntents` 按 `Monster.NextMove.Intents` 补敌人意图图标，并把 `IntentContainer.Modulate` 置白；
9. `SyncRelicCounters` 补发 `RelicModel.DisplayAmountChanged`；
10. `SyncOrbs` 按 `OrbQueue.Capacity` 补槽、按 `Orbs` 顺序塞模型；
11. `ActiveScreenContext.Instance.Update()`；
12. **最后** `SyncEndTurnButton()`：反射调 `OnTurnStarted` / `RefreshEnabled`。
    新房间的按钮初始是 Hidden 且停在屏幕外，没人发 TurnStarted 事件就永远不出现 —— 这就是
    "跳转后结束回合按钮消失"的根因。`OnTurnStarted` 内部按 `PlayerCanTakeAction` 判断，
    天然覆盖 PaelsEye 的额外回合；若恢复点已是「已按下结束回合」，改显示 `UNDO_END_TURN_BUTTON`
    并保持 Enabled，避免死局。

### 5.2 恢复完整性的判定（四层）

| 层 | 手段 | 覆盖 | v0.1.5 实测 |
|---|---|---|---|
| 校验和 | `NetFullCombatState` 的 XxHash32 | 阵容、生命/格挡/Power、五个牌堆顺序与卡牌可变属性、遗物/药水/球、回合数与阶段、**双方 RNG** | 跳转后与节点记录一致 |
| 逐字段回读 | `ObjectGraphSnapshot.Verify()` | 快照里**每一个**可写字段 + 每个集合的内容/顺序 | 2735 字段 / 610 对象，0 不一致 |
| 逐字节深比较 | `ObjectGraphSnapshot.DeepCompare()` | 浮点位、集合顺序、引用指向、结构体内容 | `deep_equal=true`，`deep_hash` 稳定 |
| 行为复现 | 从恢复点重放同一动作 | 确定性计数器、RNG、流程状态 | 两次 `PlayCardAction` 的 `action_id` 都是 0，checksum / deep hash 相同，合并回原节点 |

`LiveStateProbe`（`rew_state`）另把活状态导成 JSON：12 颗 run RNG 的 counter+状态字、玩家 RNG、
生命/格挡/能量/星/回合/阶段、五个牌堆完整卡表、药水槽、遗物与计数器、球、箭矢、敌人血量/格挡/Power/
下一步动作/意图文案，以及 `turn_state`（含 `extra_turn`）。
实测「跳到节点 2 → 跳到节点 5 → 跳回节点 2」前后 dict diff 为 0，且与节点 5 状态不同（比较有意义）。

### 5.3 全事件流水账（`CombatEventLog` / `rew_events`）

回溯树只记「稳定决策点」，「跳转 == 重放」还要求能证明中途没有事件被吞掉。
流水账按发生顺序记录：`run_started` / `combat_start` / `action_enqueued`（含 hook）/
`action_finished` / `action_cancelled` / `action_resumed` / `player_choice` / `checksum` /
`turn_start` / `capture` / `capture_merged` / `capture_skipped` / `jump_request` / `jump` /
`jump_rejected` / `prune_request` / `prune` / `prune_rejected` / `combat_end`。

每条带 `seq / kind / type / detail / action_id / round / turn / node_id`。
`rew_events` 支持 `kind` 过滤与 `last` 取最后 N 条；上限 4096 条，超出置 `truncated=true` 而非静默丢失。

实测（v0.1.5 单人局）一次出牌的流水是完整闭环：
`action_enqueued → checksum → action_finished → capture`，
`UsePotionAction` 的 `action_id` 依次为 1、2，计数器确实在递增。

v0.1.6 的关键点是**门禁只过滤建点、不丢事件**：结束回合的完整流水为
`EndPlayerTurnAction 入队/完成 → capture_skipped → checksum → ReadyToBeginEnemyTurnAction
入队/完成 → capture_skipped → checksum → turn_start Enemy → checksum ×3 →
turn_start Player → capture`。两条 `capture_skipped` 的 detail 都是
`玩家动作被禁用（结束回合过渡中）`，动作与校验和事件一条不少。
重放同一操作时整条链逐条复现，末尾 `capture_merged` 以相同 checksum 合并回原节点。

### 5.4 「跳转 == 重放」的判据

从同一节点打出同一张牌两次（中间跳走再跳回）：

- 两次 `action_enqueued` 的 `action_id` 必须相同（证明 `ActionQueueSet._nextId` 被快照恢复）；
- 两次结果的 `checksum` 与 `DeepHash` 必须相同；
- 第二次不得新增节点，而是 `capture_merged` 合并回第一次那个节点（`FindTwin` 判据生效）。

v0.1.5 实测三项全部满足：两次 `action_id=0`，checksum `e0b6a942`，
deep hash `6755e5be4f600621d94028cf61cbff71`，`node_count` 保持 3。

## 6. 落盘

```
%APPDATA%\SlayTheSpire2\global_rewind\
├── run.json                  seed、开始时间
├── combat_1\
│   ├── meta.json             遭遇战 id、开始时间
│   ├── tree.json             next_id / current_id / 全部节点（父子、标签、checksum、deep_hash、对象数）
│   └── nodes\3.json          3 号节点的摘要
└── combat_2\ ...
```

`RunManager.CleanUp`（通关、死亡、退出到主菜单）时整个目录删除。

**v0.1 的边界**：落盘的是树结构与节点摘要，**不是可再次载入的对象图**。
原因是 memento 里存的是"运行时实例引用"，跨进程没有对应实例；要跨进程恢复，必须先解决
"重建对象图"或"回到同一战斗起点再重放"的问题，这是下一阶段的工作。

## 7. 版本适配点（升级游戏后先看这里）

所有对私有成员 / 事件的依赖都集中在下面几处，游戏更新后优先检查：

| 依赖 | 位置 | 用途 |
|---|---|---|
| `NRun._roomContainer` | `CombatViewRebuilder` | 换房间时绕过 `SetCurrentRoom` |
| `NCombatRoom.BgContainer` / `Background`（私有） | `CombatViewRebuilder` | 搬运背景节点 |
| `NEndTurnButton._label` / `OnTurnStarted` / `RefreshEnabled` / `SetState` | `CombatViewRebuilder` | 恢复结束回合按钮 |
| `NOrbManager._orbs` / `AddSlotAnim` / `RemoveSlotAnim` | `CombatViewRebuilder` | 补充能球槽位 |
| `PotionModel.IsQueued` | `RewindRuntime.NormalizeAfterRestore` | 清理入队副作用 |
| `CombatManager._turnState` / `_playerActionsDisabled` | `RewindRuntime` / `MementoRules` | 回合流程与动作禁用标志 |
| `ActionQueueSet._nextId` / `ActionQueueSynchronizer._nextHookId` / `PlayerChoiceSynchronizer._choiceIds` / `ChecksumTracker.NextId` | `MementoRules` | 官方 replay 确定性计数器（多人局不加入 ChecksumTracker 根） |
| `ActionQueueSynchronizer._hookActions` / `_requestedActionsWaitingForPlayerTurn` / `CombatState`、`PlayerChoiceSynchronizer._receivedChoices` | `QuiescenceProbe` / `MementoRules` | 全体静止探针 / 本地队列阶段 |
| `ActionQueueSet.IsEmpty` / `NextActionId` / `UnpauseAllPlayerQueues`、`ActionExecutor.CurrentlyRunningAction` / `IsPaused` / `Unpause` | `QuiescenceProbe` / `RewindRuntime` | 多人静止判据与恢复后复位 |
| `CombatManager.IsPlayerReadyToEndTurn`、`CombatState.CurrentSide` / `RoundNumber` / `Players`、`PlayerCombatState.TurnNumber` / `Phase` | `PlayerScope` / `Frontier` / `QuiescenceProbe` | 切面与就绪判据 |
| `ActionQueueSet.ActionEnqueued` / `ActionResumed`、`PlayerChoiceSynchronizer.PlayerChoiceReceived`、`ChecksumTracker.ChecksumGenerated`、`GameAction.AfterFinished` / `BeforeCancelled` | `RewindRuntime` | 全事件订阅 |
| `NetFullCombatState.FromRun` / `ChecksumTracker.GenerateChecksum(NetFullCombatState)` | `CombatChecksum` | 纯哈希（不推进 NextId、不发消息） |
| `RunManager.CleanUp`、`NGame._Input` | `Patches` | 清理落盘 / 快捷键 |

`tools/Verify` 会一次性检查上面这些目标是否还在；游戏更新后先跑它。

## 8. 已知限制

1. **事件战斗**：如果战斗房间是嵌在事件布局里的（Punch-Off 之类），整房间替换可能破坏事件房间的布局。
   v0.1 未做区分，遇到异常会在日志里留下 `visual rebuild:` 记录。
2. **多人**：M0/M1/M2 已落地。`Host/Client` 下只在“所有活跃玩家 Phase=Play、无人已结束回合、
   所有队列为空、无 pending choice/hook”的全体静止点建点；节点 id 由房主分配并广播锚点，
   客户端只在同一 `checksum + frontier` 上抓自己的 memento。协同跳转是房主权威的
   `Freeze → Idle → Commit → Restored → Resume`，任一端失败则 `Abort` + 全员回滚；
   剪枝只允许房主发起，`Prepare → 全端 ready（树指纹一致）→ Commit → Done`，
   两端在同一静止点删同一棵子树。跨端只比 `checksum + frontier`；
   `local_scope_hash` 是本进程内的共享字段指纹，不能跨端比较。
   协议细节与实测见 [multiplayer-plan.md](multiplayer-plan.md) §12。
3. **敌方回合 / 回合过渡**：`IsStableDecisionPoint`（`CurrentSide` + `PlayerTurnPhase` +
   `PlayerActionsDisabled`）已挡掉所有"玩家不可操作"的时刻，这些时刻既不建点也不成为分支
   （v0.1.6）。跳转仍额外要求 `_actionDepth == 0`；`rew_status.stable` 可用于诊断。
4. **自动结束回合类模组**：SpeedX 的"无可用牌自动结束回合"会在恢复后立刻再次结束回合，属固有冲突。
5. **落盘不可跨进程恢复**（见 §6）。
6. **PaelsEye 额外回合**：`PlayersTakingExtraTurn` 已纳入 `CombatTurnState` 白名单、
   `NormalizeAfterRestore` 不再重算禁用标志、`SyncEndTurnButton` 走 `OnTurnStarted`（内部按
   `PlayerCanTakeAction` 判断），`rew_state` 也导出 `turn_state.extra_turn`。
   **已实机覆盖**：用 dev console 注入 `PAELS_EYE` 后，不打牌直接结束回合 →
   `BeforeSideTurnEndEarly` 把 10 张手牌全部 `Exhaust` → 敌方回合 → `extra_turn=["1:FOOL"]`
   且重新抽牌；跳回基准节点后额外回合、Exhaust 堆、手牌全部精确撤销；
   重放同一操作 `action_id` 0/1 与三个 checksum 全部复现并 `capture_merged` 回原节点。
   v0.1.6 补测：额外回合里消耗 5 张手牌的 hook 链同样不建过渡点，直接落到
   `extra_turn=["1:FOOL"]` 的「第 2 回合」稳定点（见 §4.1 门禁）。
7. **事件流水上限 4096 条**：超长战斗会置 `truncated=true`，此时流水账不再完整。
8. **单人局 dev console 命令不进动作队列**：`DevConsole.ProcessCommand` 只在
   `!IsSingleplayerOrFakeMultiplayer` 时才把命令包成 `ConsoleCmdGameAction` 入队；单人局
   直接本地执行 `ProcessCommandInternal`。因此 `card` / `relic` / `energy` 这类注入
   **不产生 `action_enqueued`、不推进 `nextActionId`、不触发抓点、也不改任何 RNG 计数器**——
   是纯粹的「状态编辑」。实测注入 5 张卡 + 1 遗物 + 能量后，12 颗 RNG counter 与 seed
   和注入前逐字节相同，`event_count` / `node_count` 均不变。注入后的状态必须显式
   `rew_capture` 才会成为节点；跳回注入前节点会撤销注入，跳回该节点会精确恢复。

## 9. 下一步

- 事件战斗的房间重建分支（嵌入布局 vs 独立房间）。
- 落盘格式升级：节点附带"动作路径 + 战斗起点"，冷启动时可选择重放恢复。
- 快照内存预算：超过 N 个节点或 M MB 时按最旧/最远分支淘汰，或压缩为字节流。
- 覆盖 `PlayerChoiceType.MutableCard` / `CanonicalCard` / `DeckCard` / `Index` 等其它选牌类型：
  目前实机只验证过 `CombatCard`（消耗 / 变牌 / 弃牌三类，共 5 次 `player_choice`，
  `choice_id` 均为 0 且重放复现）。
- ~~额外回合中「已按下结束回合」状态的 `UNDO_END_TURN_BUTTON` 分支~~：v0.1.6 稳定决策点
  门禁已从根源消除该场景 —— 结束回合过渡态不再建节点，回溯目标只可能是玩家可操作状态，
  此时结束回合按钮必然处于正常 `Enabled` 态（实机截图已确认「结束第 2 回合」明亮可见）。
