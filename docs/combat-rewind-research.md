# 战斗内回溯（rewind / undo）模组调研

> 调研日期：2026-11-26　目标版本：STS2 v0.113.1　关键词：不依赖操作回放，直接读写战斗状态
> 信息来源：Steam 创意工坊页面、各作者 README/DOCUMENTATION/LESSONS、sts2.gg 与 Nexus 镜像页。

## 0. 结论先行

1. **这条路已经有人走通了，而且不止一个。** 至少 6 个模组在做"战斗内撤回/前进/历史跳转"，其中 3 个明确是**直接快照状态**、非操作回放（Combat Undo、UndoTheSpire2、UndoAndRedo），1 个是混合（UndoAndRedo 主路径快照、redo 走回放），另外几个走回放或房间级存档。
2. **"直接读状态"的两种成熟做法**：
   - **原地 memento**：反射遍历整个模型对象图，把每个字段写进 memento，撤销时写回**同一批实例**（UndoAndRedo，捕获 1–4 ms / 900–1500 对象，单次 undo ≈ 100 ms）。
   - **深拷贝快照**：对可达对象做 deep clone（遗物用游戏的 `MutableClone` 省开销），撤销时替换实例并手工重建视觉（Combat Undo，30 个快照）。
   - 序列化到字节流（Spire Rewind 的 192 MiB / 64 恢复点）是第三种，适合做磁盘持久化。
3. **所有人都踩过的坑高度一致**：视觉不能"改"，只能"重建"；`NHandCardHolder` 的 `Lerp` 在 `TimeScale>1` 下会飞；入队时的副作用（药水 `IsQueued`）在快照内、必须手工撤；事件触发的战斗没有回放兜底；每个游戏小版本都要重追 API。
4. **对你的版本（v0.113.1）**：目前只有 *时序回溯 Spire Rewind* 明确声明适配 v0.113.1 public-beta，其余多数停在 v0.107.1 / v0.111.0。版本适配本身就是这个品类最大的持续成本，也是新入场的现实机会。
5. **功能空白**：没人做"战斗快照落盘（关游戏也能回）"、"分支树 / what-if 多线对比"、"战斗内复盘评分"。线性撤销已经是红海。

## 1. 工坊现状

| 模组 | 作者 | 机制 | 能力 | 版本状态 | 开源 |
|---|---|---|---|---|---|
| **时序回溯 · Spire Rewind**（3815251294） | 有神论者 | 快照（内存恢复点，192 MiB 预算，≤64 点） | 逐步撤回 / 前进 / 历史跳转 / 跨回合；多人自动停用 | **0.2.15，适配 v0.113.1 public-beta**，含愚者、急速、恐惧模式 | 未见 |
| **Combat Undo / StS2 Undo**（Nexus 716） | Inggom0111 | 深拷贝快照，遗物用 `MutableClone` | Z 单步、Shift+Z 整回合、能量旁按钮；30 快照；恢复动画/血条/意图/RNG | v0.0.14，README 标注 v0.107.1 | MIT，`ing-gom/sts2-undo-mod` |
| **UndoAndRedo**（3801936266） | luojiesi | 原地 memento + `NetFullCombatState` 校验，失败回退官方 replay；redo 走 replay | ←/→，跨回合、药水、选牌提示；单场不限深度 | 2.2.x，实测 v0.107.1 / beta v0.111.0 | 是，含 DOCUMENTATION.md + LESSONS.md |
| **Undo the Spire 2**（3747812949） | kefangc | 快照 | 出牌 / 结束回合 / 部分选择 / 药水 / 状态变化 | 适配 v0.107.1；已知图标变黑等 bug | 是，`kefangc/sts2-UndoTheSpire2` |
| **皮皮倒带 Rewind**（3747557762） | PPF | **官方 replay 回放** | 回合回溯、房间检查点、死亡回溯、Fair Mode、NoUndo 统计 | 稳定/beta 双分支 | 否 |
| **Spire Rewind - Checkpoints**（3836024192） | PingChunChung | 房间级存档检查点（重写 run save） | 回到任意层重打 + 决策日志 + HTML 回放 | 仅 v0.107.1 | 是，`PingChunChung/spire-rewind` |
| **Save & Load ++**（3811997113） | clareLab | 快照（动作/回合）+ 房间/楼层加载 | 撤销/重做、回访早期房间、重开房间/换种子 | 需 BaseLib | 是，`clareLab/sts2-slpp` |
| 其他 | — | — | *Undo And Restart with Button*、*UnDoFloor*（回退楼层）、*newbeeSL*、*Hindsight Run Resumer*（旧局重放） | — | 部分 |

旁证：工坊里已经有 **Star Runes & Undo Compatibility Fix** 这种"给撤销类模组打兼容补丁"的模组，说明这类模组的用户量和冲突面都不小。

## 2. 三条技术路线对比

| | 原地 memento | 深拷贝快照 | 官方 replay 重放 |
|---|---|---|---|
| 代表 | UndoAndRedo | Combat Undo、UndoTheSpire2 | 皮皮倒带、UndoAndRedo 的 redo |
| 捕获成本 | 1–4 ms（900–1500 对象） | 数 ms～数十 ms（克隆 + 分配） | 0（只记事件） |
| 恢复成本 | ~100 ms（写回 + 校验 + 重建 UI） | ~100 ms 量级 | 0.4–1.5 s，且随回合数线性增长 |
| 状态完整性 | 最高（每个字段） | 高（需枚举可达对象并正确克隆） | 依赖重放确定性，事件战斗无法重建 |
| 内存 | 大（对象图 memento） | 大（克隆对象） | 极小 |
| 事件触发的战斗 | 可用 | 可用 | **不可用**（房间栈无法重建） |
| 磁盘持久化 | 需自己序列化 | 天然可序列化 | 只需存种子 + 事件流 |
| 主要风险 | 漏字段 / 排除项没排干净 | 共享引用、循环引用、克隆语义 | 重放不确定性、动画与 UI 时序 |

**对"直接读状态"的选型建议**：主路径用原地 memento（最不容易漏、事件订阅天然保持有效），把"序列化快照"只用于落盘；replay 只当校验/兜底，不要当主路径。

## 3. 已被验证的工程结论（来自 UndoAndRedo 的 DOCUMENTATION / LESSONS）

这部分比功能列表值钱，直接决定能不能做成。

### 3.1 快照范围

- 从 `RunState`、`CombatManager`、卡牌库、`ActionQueueSet`/`ActionExecutor`、`ChecksumTracker`、`CombatReplayWriter` 出发做可达性遍历，记录**每个实例字段与数组元素**。
- **绝不手写字段清单**。作者原话：v1 手挑状态是"无法修复的错误"。
- 明确排除：Godot 对象、委托/事件、`Task`/线程原语、反射元数据、logger、**canonical（不可变）模型**。
- 恢复后要把模型里指向**已释放 Godot 对象**的缓存字段置空（`ClearDisposedGodotReferences`），这些是懒加载 getter，会自行重建。

### 3.2 校验与捕获点

- 捕获条件：某个玩家决策开始执行前，且该决策是**队列里唯一的动作**、无 pending resumption。
- 校验 oracle：恢复后 `NetFullCombatState` 的 checksum 必须等于捕获时记录的值；不等就把 live state 写回、退回 replay 路径。**没有任何未校验的状态会被应用。**
- 注意 `BeforeActionExecuted` 在"等待选牌后恢复"时会二次触发，必须用 `action.State == WaitingForExecution` 过滤，否则把"动作中途"的 checksum 当成了"动作前"。

### 3.3 视觉：重建，不要修补

- 直接换掉整个 `NCombatRoom`（`NCombatRoom.Create` + 自身 `OnCombatSetUp`），让游戏自己建生物节点、血条、能量、结束回合按钮；随后补手牌、意图、充能球、药水槽、顶栏、遗物计数、`NEndTurnButton.OnTurnStarted`。
- **背景节点必须从旧房间搬运**，不能重建：部分 boss（Kaiser Crab）由背景绘制且怪物模型缓存该节点。
- 旧房间释放会解除其节点上的所有事件订阅，这正是"重建"比"打补丁"干净的原因。
- 时机：房间必须等 UI 有状态后再设为激活屏幕，否则 `NCombatUi.Enable` 崩；手牌要等每张牌落位（≥5 帧、误差 2 px 内）再揭遮罩。

### 3.4 已知坑（逐条）

| 现象 | 根因 | 处理 |
|---|---|---|
| 闪屏、手牌乱飞、结束回合卡住 | `NHandCardHolder` 用 `Lerp(target, delta*7)`，权重没钳制，`TimeScale>1` 时发散 | prefix 直接把 holder 吸附到目标位并重新启用 hitbox |
| 撤销后药水不能使用/丢弃 | `PotionModel.EnqueueManualUse` 在入队**之前**置 `IsQueued=true`，该副作用被快照记下 | 撤销后调用游戏自己的取消路径 |
| Kaiser Crab 双 boss 不可见、再打一下 `ObjectDisposedException` | 背景被重建，模型缓存指向已释放节点 | 搬运旧背景 + 清空失效 Godot 引用 |
| redo 后敌人回合执行两次 | 重放时网络服务必须报告 `Replay`，否则 `RequestEnqueue` 会再入队 `ReadyToBeginEnemyTurnAction` | 给 `NetSingleplayerGameService.Type` 打 postfix |
| beta 分支模组加载失败 | 模组里实现了游戏接口，接口加成员后 `Assembly.GetTypes()` 抛异常 | **不要在模组里实现游戏接口** |
| 撤销后立即又结束回合 | SpeedX 的"无可用牌自动结束回合"在恢复态上再次触发 | 文档化，测试时关闭该选项 |

### 3.5 性能与内存

- 捕获 1–4 ms，写回 ~1 ms，端到端 undo ≈ 90–120 ms（与深度无关）。
- 回放路径：固定 ~260 ms + 每回放回合 ~120 ms。
- 想靠 `Engine.PhysicsTicksPerSecond = 1` 提速 → 引擎崩溃 + 内存无界增长，**不要动**。

## 4. 和战斗状态集的对应关系

上一轮整理的七层状态集，正好对应 memento 的覆盖范围：

| 层 | memento 是否覆盖 | 备注 |
|---|---|---|
| L0 运行游标（seed/act/coord、`CombatId`、`_nextCreatureId`） | ✅ | 反射遍历天然覆盖，前提是根对象选对 |
| L1 战斗 meta（`RoundNumber`、`CurrentSide`、Encounter） | ✅ | 官方 checksum 不含 `RoundNumber`/`CurrentSide`，所以**校验通过 ≠ 完全等价** |
| L2 生物（HP/Block/Powers 含 `InternalData`） | ✅ | 这正是 memento 比官方 checksum 强的地方 |
| L3 玩家战斗态（5 牌堆有序、能量、星、充能球） | ✅ | 顺序必须保序 |
| L4 模型实例（卡/遗物/药水可变字段） | ✅ | 排除 canonical 模型即可 |
| L5 RNG（12+3+每怪 1 条，counter + s0..s3） | ✅ | 漏一条就不可复现 |
| L6 在途与历史（`CombatHistory`、队列、`PendingLoss`） | ⚠️ | 队列状态取决于捕获点；历史是普通字段，能覆盖 |

结论：**用 memento 不用逐字段枚举**，但要清楚 checksum 只覆盖其中一部分——校验通过不代表 `RoundNumber` 或怪物的 `NextMove` 对了，测试要自己补断言。

## 5. 空白与建议

### 5.1 已被占满的定位

- "Z 撤回一步 / Shift+Z 撤回整回合"：至少 4 家在做了。
- "回到本场战斗开始"：同上。
- "回到某一层"：`UnDoFloor`、`Save & Load ++`、Spire Rewind Checkpoints 都在做。

### 5.2 仍然空着的

1. **快照落盘**：现有内存恢复点（64 个 / 192 MiB）退出游戏即失。把战斗快照序列化到磁盘（按 `ModelId + SavedProperties` 的规范形式），就能做到"关掉游戏，明天回来从第 7 回合继续推演"。技术上要解决的是序列化/反序列化后**实例身份**的重建（卡牌实例 → 牌堆引用），比内存 memento 难一档，但没人做。
2. **分支树 / what-if**：所有实现都是线性 undo/redo，新动作一发生就清空 redo。做成树（同一状态 fork 多条线、并行比较结局）是明显的差异化，UI 也有得做。
3. **战斗内复盘**：逐步记录 + 评估（这步的期望伤害、抽牌质量、能量利用率），类似棋类复盘。工坊有 `RngPredictor`（悬停预测随机结果）和 Skada（伤害统计），但没有把两者合成"复盘"。
4. **多人战斗内回溯**：`QuickLink` 只做到房间级 checkpoint + 自动重连；战斗内 rewind 需要全员同步 + host 权威，而且 `affects_gameplay:true` 会直接挡住联机。难度最高，不建议作为第一版。

### 5.3 如果要做，最小可行路径

1. **先跑通"捕获 → 写回 → 校验"**，不做 UI：在 `BeforeActionExecuted`（`WaitingForExecution`）时反射 dump 对象图，改一个字段后写回，用 `NetFullCombatState` checksum 自证。
2. **再做视觉重建**：整房间替换，先把"手牌 + 血条 + 意图 + 能量"四样做对，其余（药水槽、充能球、遗物计数）按需补。
3. **最后做交互**：热键 + 历史列表，加"捕获失败/校验失败就静默禁用"的降级，别让游戏崩。
4. **版本门禁**：`[ModInitializer]` 里用反射探测所有依赖的私有成员，缺失就整体停用并在设置里显示原因——这是这个品类里唯一能长期活下来的做法（参考 UndoAndRedo 的 `Reflection:` 日志行）。
5. **兼容性**：`affects_gameplay` 写 `true`（会挡联机，但这是正确语义）；注意与 SpeedX（自动结束回合）、RegentFX 之类改写视觉/战斗流程的模组冲突。

## 6. 参考链接

- 时序回溯 Spire Rewind：https://steamcommunity.com/sharedfiles/filedetails/?id=3815251294
- Combat Undo：https://www.nexusmods.com/slaythespire2/mods/716 ／ https://github.com/ing-gom/sts2-undo-mod
- UndoAndRedo：https://steamcommunity.com/sharedfiles/filedetails/?id=3801936266 ／ https://github.com/luojiesi/SLS2Mods
- Undo the Spire 2：https://steamcommunity.com/sharedfiles/filedetails/?id=3747812949 ／ https://github.com/kefangc/sts2-UndoTheSpire2
- 皮皮倒带 Rewind：https://steamcommunity.com/sharedfiles/filedetails/?id=3747557762
- Spire Rewind - Checkpoints：https://steamcommunity.com/sharedfiles/filedetails/?id=3836024192 ／ https://github.com/PingChunChung/spire-rewind
- Save & Load ++：https://steamcommunity.com/sharedfiles/filedetails/?id=3811997113 ／ https://github.com/clareLab/sts2-slpp
