# Global Rewind

杀戮尖塔 2（Slay the Spire 2）的**全局操作树回溯**模组。

## 定位

把一局游戏里每一次可决策的操作记成**操作树**上的一个节点，随时跳到树上任意节点继续——
不是"读一个存档点"，也不是"把操作再打一遍"。

- **操作树**：出牌、用药水、结束回合、选牌（消耗 / 变牌 / 弃牌 / 保留）……每个决策点都是一个节点；
  树可以分叉、可以剪枝（右键节点）、可以折叠（左键连线）。
- **原地回溯**：跳转直接读写战斗模型（memento），**不做操作回放**，所以跳转是常数时间，不依赖 RNG 重演。
- **可验证**：跳转后逐字段回读 + 逐字节深比较 + 校验和三重核对，任一项不一致就回滚，
  不会给出"看起来恢复了、其实已经偏了"的局。
- **可协同**：多人战斗里由房主分配节点编号，协同跳转要全员确认才提交，失败全员回滚。

**当前版本（v0.1.15）只支持战斗内回溯。** 战斗外的地图、商店、事件、篝火等节点尚未接入，
所以现在还做不到"整局任意回溯"——这是这个模组的长期目标，不是当前能力。

| 能力 | 当前版本 |
|---|---|
| 战斗内建点 / 任意跳转 / 逐字节校验 | ✅ |
| 操作树可视化（圆点 + 连线折叠、传送门节点、剪枝） | ✅ |
| 多人战斗内协同跳转（房主权威、失败全员回滚） | ✅ |
| 内置 MCP 控制面（供外部脚本 / Agent 调用） | ✅ |
| 战斗外节点（地图 / 商店 / 事件 / 篝火） | ❌ 计划中 |

> 联机注意：清单里 `affects_gameplay: true`，**所有玩家必须装同一版本**，否则游戏握手会拒绝联机。

## 安装

需要游戏 **≥ 0.113.1**（清单里的 `min_game_version`）。本模组**没有第三方依赖**，不需要 BaseLib。

### 手动安装

1. **完全退出游戏**（运行中的进程会锁住已加载的 DLL）。
2. 取两个文件：`dist/GlobalRewind.dll` 与 `dist/GlobalRewind.json`（清单）。
   仓库里已经放了发布用的二进制，每次 Release 构建会自动刷新；也可以按下面的「从源码构建」自己生成。
3. 在游戏根目录下建 `mods/GlobalRewind/`，把两个文件放进去：

   ```
   <游戏根目录>/mods/GlobalRewind/GlobalRewind.dll
   <游戏根目录>/mods/GlobalRewind/GlobalRewind.json
   ```

   常见路径：

   - Windows：`C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\GlobalRewind\`
   - Linux：`~/.steam/steam/steamapps/common/Slay the Spire 2/mods/GlobalRewind/`

4. 启动游戏。首次以带模组的方式启动时，游戏会自己弹一次确认（原版行为）；
   进入战斗后按 `F7` 召出探索树。

> 目录名随意，但 `GlobalRewind.dll` / `GlobalRewind.json` 必须与清单里的 `id` 同名、放在同一目录。
> 游戏内置加载器会扫描 `mods/` 下的任意子目录（也支持 `mods_STEAMTEST/` 与创意工坊目录）。

### 从源码构建并自动部署

构建脚本跨平台，只依赖 .NET SDK 9+，会自动找 Steam 库里的游戏目录与 `data_sts2_*` 数据目录：

```bash
bash build.sh                  # Linux / macOS / Git Bash：编译并部署
powershell -File build.ps1     # Windows PowerShell
build.cmd                      # Windows cmd
dotnet run --project build --  # 任意平台直接调用
```

部署目标就是上面的 `<游戏根目录>/mods/GlobalRewind/`。只编译不部署用 `--no-deploy`；
找不到游戏目录时显式指定：

```bash
bash build.sh --game "D:/Steam/steamapps/common/Slay the Spire 2"
STS2_GAME_DIR="/path/to/Slay the Spire 2" bash build.sh
```

Release 构建成功后会自动刷新 `dist/GlobalRewind.dll` 与清单副本 —— 那是给人直接下载安装的发布二进制，
发版时连同 `dist/` 一起提交（托管程序集与平台无关，一份就能在 Windows / Linux / macOS 上用）。

## 卸载

1. **完全退出游戏**。
2. 删除 `<游戏根目录>/mods/GlobalRewind/` 整个目录（DLL、清单一并在里面）。
3. 可选：清理本地残留数据（不删也不影响游戏，只是占空间 / 留着旧设置）
   - `%APPDATA%\SlayTheSpire2\global_rewind\` —— 当前这局的战斗快照（整局正常结束时会自动删除）
   - `%APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg` —— 面板位置 / 尺寸 / 缩放 / 透明度 / 键位
   - Linux / macOS 上把 `%APPDATA%` 换成 `~/.config`
4. 联机前确认队友也不再启用（模组列表不一致会被拒绝）。

## 禁用（保留文件，随时再打开）

游戏内置加载器没有模组开关界面，禁用走下面任意一种：

1. **改设置文件**（推荐，最干净）
   编辑 `settings.save` 里 `mod_settings.mod_list` 中对应条目，把 `is_enabled` 改成 `false`：

   ```json
   { "id": "GlobalRewind", "is_enabled": false, "source": "mods_directory" }
   ```

   文件位置：

   - Steam：`%APPDATA%\SlayTheSpire2\steam\<SteamID>\settings.save`
   - 离线 / 非 Steam：`%APPDATA%\SlayTheSpire2\default\1\settings.save`

   改完重启游戏。`source` 必须与安装位置一致：手动装在 `mods/` 下是 `mods_directory`，
   创意工坊是 `steam_workshop`，否则这条记录不会命中。
2. **移出 `mods/` 目录**：把 `mods/GlobalRewind/` 改名（如 `GlobalRewind.off`）或移到游戏目录外，
   加载器扫描不到就不会加载。
3. **临时全部禁用**：启动参数加 `nomods`，本次启动不加载任何模组。

禁用后：战斗里没有面板、`F7` 无效、内置 MCP 的 `8766` 端口不会监听。
已落盘的快照仍留在 `%APPDATA%\SlayTheSpire2\global_rewind\`，重新启用后还能读。

## 使用

| 默认键 | 作用 |
|---|---|
| `F7` | 打开 / 关闭探索树（仅局内有效） |
| `F6` | 回到父节点（撤销一步） |
| `Shift + F6` | 前进到最近子节点 |

树上的左键跳转、右键剪枝、点连线折叠、拖拽标题栏、`Ctrl + 滚轮` 缩放等操作见
[UI 与快捷键](#ui-与快捷键)；内置 MCP 端点与工具表见 [MCP 控制面](#mcp-控制面)。

## 版本状态与变更记录

> 目标版本：Slay the Spire 2 v0.114.0（.NET 9 / Godot 4.5）
> 状态：**v0.1.15**，修复多人局客户端回溯树为空（早到的锚点被丢弃 → 根节点缺失 → 后续锚点全部建点失败）；
> v0.1.14 允许删主节点子树（删完自动重选主节点），同层传送门连线走下半层、直接进圆点；
> v0.1.13 面板可拖动/可改尺寸，传送门虚线改为"底部出线、落在目标上方边中点"；
> v0.1.12 新增**传送门节点**（跨分支同态状态只保留一个可展开的规范节点，
> 规范节点取最深的一个，较浅的变成可点击的 `≡` 叶子，虚线指向规范节点；出现更深的同态节点会立刻换主），
> 原有抓点 / 跳转 / 合并语义不变；抓点绑定**动作逻辑边界**（动作 `Execute` 结束、下一个动作尚未开始），
> 快速连点 / 出牌后立刻结束回合也逐次建点；动画只参与跳转后的画面重建。
> 多人（战斗内）M2：房主分配节点编号并广播锚点，客户端按 checksum 命中边界快照缓存镜像；
> 协同跳转 `Freeze → Idle → Commit → Restored → Resume`，失败全员回滚；多人剪枝由房主发起，
> 双端在静止点校验树指纹后同时剪枝。
> 单人局的稳定决策点门禁、全事件记录、官方 replay 确定性计数器入快照、跳转/重放逐字节等价保持不变。

**v0.1.15 多人客户端回溯树为空**：

- **现象**：真联机里房主侧的树正常，客户端的树永远是空的（面板只显示「本场战斗还没有节点」），
  房主的节点在 4 秒后全部变成 `partial`（不可跳）。
- **根因**：客户端不自己建点，整棵树都靠房主广播的锚点镜像。房主在**自己的** `CombatBegan` 里就抓了
  根节点并广播锚点，而客户端这时还在加载战斗房间（`_active == false`），锚点只能先挂起；
  紧随其后的 `OnCombatBegan` 却把 `_pendingAnchors` 清空了 —— 根节点（`parent = 0`）一丢，
  后续每个锚点的父节点在本端都不存在，`AddWithId` 全部失败，客户端自然一个节点都没有。
- **修法**：
  - `OnCombatBegan` 不再丢弃早到的锚点，只重排父链、把重试窗口从战斗就绪时刻重新计时
    （事件流水新增 `mp_anchor_early` / `mp_anchor_kept`）；
  - `OnAnchor` 去掉重复入队（同一个锚点被排了两次，每次多一轮探测和一次多余 ack）；
  - 锚点超时兜底不再假设父节点一定存在：父节点缺失时沿收到过的锚点父链挂到最近的已落地祖先
    （父链也断就挂到现有根；本端还没有根就由它占位），保证「只要收到过锚点，树就不可能整体不可见」，
    这类节点仍标 `partial` 并写 `mp_anchor_orphan`，剪枝会因为树指纹不一致而明确拒绝，不会悄悄分叉；
  - checksum 二次核对提前到建点之前，避免先落一个与房主不一致的镜像节点再把它 ack 成 ok。
- 回归点：客户端日志顺序应为 `mp_anchor_early`（可选）→ `mp_anchor_kept`（可选）→ `mp_anchor_cache_hit` /
  `mirror` → 房主 `mp_anchor_shared`；两端节点 id / 父节点 / checksum 一致。

**v0.1.14 剪枝与同层连线**：

- **允许删主节点子树**：不再因为"子树外还有传送门指向它"而拒绝。删完立刻重选主节点
  （`RewindTree.ReelectCanonicals`）：
  - 剩余传送门里**最深**的一个转正（同深度取小 id），其余改指它并共享同一份快照；
  - **只剩一个传送门时它直接变回普通节点**（`AliasOf = 0`），圆点里重新显示编号；
  - 判据只有 `Depth` 与 id，多人两端重放同一次剪枝结果完全一致，不需要新消息。
- **同层传送门连线**：源与目标在同一层时，折线只走**下半层**（`y + 42`），到目标正下方竖直进入
  **圆点下缘**，箭头朝上；跨层仍然落在目标上方那条边的中点（箭头朝下）。
- 测试脚手架：`rew_test_portal`（`GLOBAL_REWIND_TEST_HARNESS=1`）造一个传送门，便于验证连线与重选；
  实机已验证「两个传送门 → 剪主节点 → 最深者转正、另一个改指」「只剩一个 → 转正为普通节点并可跳转」。

**v0.1.13 UI 调整**：

- **面板可拖动**：标题栏、副标题、底栏和四周留白都能拖（图区/滑杆/按钮各自保留原语义），
  松手后位置落盘（`panel_x` / `panel_y`）。
- **面板可改尺寸**：右下角有三道斜线的握把，拖动改宽高，范围 `460×300` ~ 视口尺寸；
  松手落盘（`panel_w` / `panel_h`），下次召出保持；换分辨率时自动收回视野内。
- **传送门虚线改版**（`TreeLayout.LinkRoutes`）：
  - 从传送门**底部**出线，竖直落到下一层空带的**中线**（两行之间没有节点）；
  - 在空带里横移；需要跨层时，在目标旁的**空位中点**（兄弟间距 30px 的正中）竖直走；
  - 落点是**目标上方那条边的中点**（`t.y - 42`），箭头朝下（`∨`）；
  - 目标就在下一层时整条线只有一个直角；目标被折叠时同样收口到空心圆上方的边；
  - 不再使用外缘 lane，画布宽度回到纯主干 bbox。
  - 已知取舍：横段与同层树总线共线（虚线压在全线之上），详见 `docs/dag-visualization.md` §11.15。
- 测试：`LinkScenarioTest` 的断言改为"起点在传送门底部 / 第二点在空带中线 / 终点在目标上方边中点"，
  四个几何场景 + 5 轮随机布局全绿。

**v0.1.12 传送门节点（实现状态）**：

- 同态节点不再限于兄弟：全局按 `checksum + DeepHash/MergeHash`（多人再加 frontier）找规范节点。
- **规范节点 = 同态节点里最深的一个**；同深度保留先出现的。更深的同态节点出现时立刻换主：
  旧主的子树整体改挂到新主、旧主降级成叶子传送门，所有指向旧主的传送门改指新主。
- 传送门是叶子、永远不会成为 current，点击等同点击规范节点（`JumpTo` 会解析 alias）。
  与规范节点**共享同一个快照实例**，一次合并只多一个几十字节的节点，不多一份 memento。
- 视觉：32px 槽位里画 22px 空心圆 + `≡`，虚线 + 箭头指向规范节点；规范节点加一道细白环；
  左键点线聚焦来路、右键点线删除传送门；折叠后连线单跳收口到空心圆。
- 剪枝：传送门是叶子直接删；规范节点若还有子树外的传送门指向它，拒绝并点名。
- 多人协议 `Version = 2`：`GrAnchor` 增加 `alias_of` / `demote_old_id`，**两端必须同版本**。
- **合并判据分两层**：先比 `checksum + DeepHash/MergeHash`（逐字节相同）；
  不成立再比 `checksum + Frontier.SameStateAs`（回合/侧/阶段/就绪/额外回合，忽略动作编号），
  命中时标 `MergeExact=false`（"等价同态"）。必须这样做的原因：`CombatHistory` 按顺序记录出牌/受伤/抽牌，
  同样的牌换序打完、洗牌回到同一局面时 checksum 相同、DeepHash 必然不同。
- 测试：`tools/UiLayoutTest` 新增 4 个连线几何场景（同层/更深/跨 3 棵子树/折叠收口）全绿。
- **v0.1.12 实机验证（只用打击/防御，洗牌后的回合开始）**：
  - 传送门：第 4 回合按 `S,S,D` 与 `D,S,S` 两种顺序打完并结束回合，第 5 回合开始的两个节点
    `checksum=27da4130` 相同（血量 73、敌方 12 也一致）；第二次重放得到
    `capture_portal portal #21 -> canonical #17 (depth 16 vs 16)`，current 回到 #17，
    树里只多一个 `≡` 叶子，没有长出重复子树。
  - 换主：用测试脚手架把 #17 上移一层（depth 16→15），再重放同一局面的另一条路径，得到
    `capture_canonical_switch node #22 took canon from #17, moved 1 children`；
    #17 变成 `alias_of=22` 的叶子，原来的子节点 #18 改挂到 #22 下，
    `rew_jump 17` 解析到 #22（`requested_id=17, current_id=22`）。
  - 测试脚手架：`rew_test_shrink_depth`（需 `GLOBAL_REWIND_TEST_HARNESS=1`），只用于制造
    "更深的同态节点"；正式游玩不会用到。
- **仍未覆盖**：多人下 `alias_of` / `demote_old_id` 的双实例联调；传送门在折叠区里的收口观感。

**v0.1.11 动作边界抓点实机结论**：

- **快速连发**（`rew_test_burst` 一次入队 2 张，事件流里两个 `action_enqueued` 都在第一个
  `action_finished` 之前）：树里出现 `第 1 回合 → 打出 STRIKE → 打出 BASH` **两个 card 节点**；
  修复前只会留下最后一张 BASH。
- **出牌后立刻结束回合**：卡牌节点保留，之后只多一个「第 2 回合」节点；
  结束回合动作本身仍是 `capture_skipped`（阶段 End / 过渡态），不会变成节点。
- **跳转中间节点**：跳到「打出 STRIKE」节点 `verify_mismatches=0`、`deep_equal=true`，手牌/敌人血量
  与该节点一致；从该节点重放同一张 BASH 会 `capture_merged` 回已有节点（合并用“忽略已分配动作编号”的
  合并哈希，避免因为排队动作的 id 不同而多出重复节点）。
- **真联机**：房主连发 2 张，双端都得到 `#3 STRIKE / #4 BASH`，id / checksum / `state=shared` 完全一致；
  客户端日志是 `mp_mirror_cached → mp_anchor_cache_hit → mirror`，说明锚点是按 checksum 命中本端
  在动作边界抓的镜像快照，而不是等“本端也静止”。客户端发起连发同样双端一致。
- 单元回归：结束回合只新增一个 `turn_start` 节点，`capture_skipped` 只有一条（不再被怪物动作刷屏）。

**已知限制（v0.1.11）**：如果点击结束回合的 UI 在卡牌结算瞬间就读了手牌费用，卡牌模型会惰性创建
`CardEnergyCost` 对象。此时“重放同一张牌”的节点与原始节点对象图多出这些缓存对象，合并会失败，
树上会出现一个内容相同、额外的兄弟节点。节点本身仍然有效、可跳转；这不是状态错误，属于展示缓存的
归一化问题，后续可以把惰性缓存按“默认值等价于 null”纳入合并哈希。

**v0.1.10 多人剪枝实机结论**（双实例 ENet）：

- 房主在分支点（节点 3/4 同为节点 2 的子节点、当前在 4）剪掉节点 3：
  `rew_prune` 返回 `pending=true, request_id=1`，`rew_prune_status` 一次轮询内 `ok=true, phase=done, removed=1`；
  双端树都只剩 `1 / 2 / 4`、`current=4`、`rew_verify` 0 不一致；
  双端 `nodes/3.json` 都消失（房主 `%APPDATA%`、客户端 `APPDATA` 各自独立，不再写同一份）。
- 客户端调用 `rew_prune` 直接拒绝：`多人局只有房主可以剪枝`。
- 一方已结束回合时房主剪枝被拒绝：`请在全体静止点剪枝：1000:IRONCLAD 已结束回合`。
- 根节点拒绝（`根节点不能剪枝`）；剪当前节点所在路径拒绝（`当前节点在该子树里，先跳到其它分支再剪`）。
- 客户端事件流水：`mp_prune_ready_sent`（带 `nodes=4 hash=…`）→ `mp_prune`（`removed=1 nodes=3`）；
  房主侧多一条 `mp_prune_commit`（`hash` 与 ready 阶段的期望一致）。

**v0.1.9 多人 M2 实机结论**（双实例 ENet：`--fastmp host_standard` + `--fastmp join --clientId 1000`）：

- 双端节点完全一致：`combat_start / turn_start / card / turn2` 四个节点 id、`checksum`、`state=shared` 全同；
  客户端是按房主锚点在**同一个 checksum + frontier** 上抓自己的 memento，不是收快照。
- 房主发起协同跳到第 1 回合开始节点：`rew_jump` 返回 `pending=true, request_id=1`，
  `rew_jump_status` 一次轮询内变成 `ok=true, phase=resumed`；双端 `rew_verify` **0 不一致**、
  `deep_equal=true`、`rew_checksum` 都等于目标节点的 `checksum`。
- 跨回合跳转（第 2 回合 → 第 1 回合）成功；跳完再按结束回合，双端直接落到**同一个**第 2 回合节点
  （`checksum 4a26a573`，`capture_merged` 到已有节点），证明“跳回 + 重放 == 原始推进”在真联机下成立。
- 客户端也可以发起：`rew_jump` 只是向房主请求，房主统一广播 Freeze/Commit/Resume；
  房主拒绝时回 `GrAbort`，客户端拿到明确原因。
- 一方已结束回合（非静止点）时请求跳转：双端 `quiescent=false`，请求等待约 8 秒后
  `aborted`，原因是「本端未能到达静止点：1000:IRONCLAD 已结束回合」，不会硬跳。
- 故障回滚实测：给客户端装 `rew_test_poison_restore`（写回成功后强制判定失败）后跳转，
  客户端 `mp_restored ok=false` → 房主 `mp_abort rollback=True` → 房主 `mp_rollback`，
  双端回到跳转前的节点与 `checksum`、`rew_verify` 仍为 0 不一致，**没有人被踢**。
- 重放同一张牌：房主 `capture_merged` 回原节点并发 `GrCurrentMessage`，客户端 `current_id` 跟着推进，
  两端“我在哪个节点”不会漂移。


**v0.1.8 多人 M0+M1 实机结论**：

- **假多人**（单进程 2 人，本地玩家故意放 1 号位）：`rew_status` 能看到两个玩家的 phase/energy/ready，
  根节点 `phases=None/None, playable=false`，回合开始节点 `Play/Play, playable=true`；
  `rew_test_hold` 给 dummy 入队占位动作后 `quiescent=false`、`rew_capture` 返回“还有动作在队列里没结算”，
  release 后恢复；`rew_jump` 到回合开始节点 `verify_mismatches=0`、`deep_equal=true`、共享字段校验 0 不一致；
  根节点跳转被拒绝。
- **真联机**（一台机器双实例：`--fastmp host_standard` + `--fastmp join --clientId 1000`，ENet 33771）：
  双端 `Frontier` 除 `local_net_id` 外完全一致；root / 第 1 回合 / 出牌 / 第 2 回合四个节点的
  `checksum` 双端一致（`8df35d06` / `cca53206` / `7e99e692` / `3cc5a0b1`）；
  出牌后双端各建同名节点；双端结束回合后直接落到第 2 回合节点，没有过渡节点；
  `rew_jump` 双端都返回“多人局 M1 只支持采集/验证，协同跳转在 M2 实现”。
- 注意：MCP/落盘里的 `local_scope_hash` 是**本进程内**的共享字段指纹，双端不一样是正常的；
  跨端只比 `checksum` + `frontier`。

**v0.1.7 UI 实机结论**（从主菜单 `continue_run` 进入既有存档）：

- 节点只显示编号，悬停才出现 `#id / 类型 / 回合 / 校验和 / 对象与字段数 / 子节点数`；
- 第一个子节点在父节点正下方，其余子节点同高向右排，互不重叠（`#2` 下挂 `#3`、`#5`，`#5` 位于 `#3` 子树最右节点的右侧）；
- 点击连线折叠子树，折叠后变为**无编号空心圆**，悬停显示节点数，点击恢复；折叠状态按节点 id 保存，
  外层展开后里层仍然折叠（`#3` 折叠 → `#2` 折叠 → 展开 `#2`，`#3` 仍是空心圆）；
- `Ctrl + 滚轮` 以鼠标位置为锚缩放（50%–200%），`+/-` 按钮以视口中心缩放；滚轮/滚动条双向滚动；
- 标题栏可拖拽，背景不透明度 20%–100% 可调，设置落盘到 `%APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg`；
- 按键可在面板内重新绑定（实测 F7 → F9，重启后仍生效；Esc 取消）；
- 右上角 `RW #id · n` 文字入口已移除，且只有局内（`RunManager.IsInProgress`）才能用热键召出。

**v0.1.6 实机结论**（从主菜单 `continue_run` 进入既有存档，dev console `relic remove PAELS_EYE` 还原「无佩尔之眼」场景）：

- 点击结束回合后动作链为
  `EndPlayerTurnAction` → `capture_skipped` → `ReadyToBeginEnemyTurnAction` → `capture_skipped` →
  敌方回合 → `turn_start Player` → `capture`，树里**只新增「第 2 回合」一个节点**；
  v0.1.5 会多出「结束回合」「ReadyToBeginEnemyTurnAction」两个中间过渡节点。
- 两次 `capture_skipped` 的原因都是「玩家动作被禁用（结束回合过渡中）」；
  所有动作 / 校验和 / 回合事件仍完整记录 —— 门禁只过滤**建点**，不丢事件。
- 带 PaelsEye 的额外回合同样验证：消耗 5 张手牌的 hook 链不建过渡点，直接落到
  `extra_turn=["1:FOOL"]` 的「第 2 回合」稳定点。
- 重放同一操作：事件链逐条对应，`capture_merged` 以相同 checksum 合并回原节点，`node_count` 不变。
- `rew_jump` `verify_mismatches=0` / `deep_equal=true`；`rew_verify` 2876 字段 / 638 对象全匹配；
  跳走再跳回 `rew_state` dict diff = 0；日志 0 条 WARN/ERROR；截图「结束第 2 回合」按钮明亮可见。

**v0.1.5 实机结论**（从主菜单 `continue_run` 进入既有存档，游戏侧 MCP 操作、模组侧 MCP 回溯）：

- `rew_jump` 全部返回 `verify_mismatches=0`、`deep_equal=true`：
  实测 `verified 2735 field(s) across 610 object(s): all match`。
- 「跳到节点 2 → 跳到节点 5 → 跳回节点 2」前后 `rew_state` dict diff = **0**，且与节点 5 状态不同。
- 从同一节点重放同一张牌：两次 `action_enqueued` 的 `action_id` 都是 **0**（确定性计数器被精确恢复），
  checksum `e0b6a942`、deep hash `6755e5be4f600621d94028cf61cbff71` 完全一致，
  第二次 `capture_merged` 合并回原节点，`node_count` 不变。
- 全事件流水闭环：一次出牌 = `action_enqueued → checksum → action_finished → capture`；
  `UsePotionAction` 的 `action_id` 依次 1、2。
- 跳转后「结束第 1 回合」按钮明亮可见，手牌 5 张、能量 3/3、HP 20/77、金币 118 全部正确。
- 剪枝：当前所在分支被拒绝，兄弟分支成功删除 3 个节点；`tools/Verify` **ALL CHECKS PASSED**。
- 运行期日志无 `[GlobalRewind] WARN` / ERROR。

**v0.1.5 补测（dev console 注入，未改源码）**：用游戏内 dev console（`` ` `` / `~` 打开，
modded 环境下 debug 命令已允许）注入 `PAELS_EYE` 与 5 张选牌卡，补齐上一轮未实机覆盖的场景：

- **RNG 中性**：注入 5 张卡 + 1 遗物 + 能量后，12 颗 RNG counter 与 seed 和注入前逐字节相同；
  `event_count` / `node_count` 均不变 —— 单人局 console 命令直接本地执行，不进 `ActionQueueSet`，
  也不推进 `nextActionId`。注入是纯粹的「状态编辑」。
- **跳转撤销 / 恢复注入**：显式 `rew_capture` 固化基准节点 #6 → 跳回 #2 完全撤销注入
  （手牌 5、能量 3、遗物 2）→ 跳回 #6 精确恢复（手牌 10、能量 12、佩尔之眼），
  `rew_state` dict diff = **0**、`deep_equal=true`、`rew_verify` 0 差异。
- **PaelsEye 额外回合**：不打牌结束回合 → 10 张手牌全部进 `Exhaust` →
  `turn_state.extra_turn=["1:FOOL"]` 并重新抽牌；跳回 #6 精确撤销；重放时
  `action_id` 0/1、checksum `6b83d256` / `9d7ecce0` / `1ff61f44` 全部复现，
  3 条 `capture_merged` 合并回原节点，`node_count` 不变。
- **四类操作全覆盖**：`BRAND`（消耗）、`BEGONE`（变牌，手牌变 `MINION_STRIKE`）、
  `DAGGER_THROW` / `ACROBATICS`（弃牌）、`EQUILIBRIUM`（保留手牌：无需选牌，
  但状态变化被 `capture` 捕获）。共 5 条 `player_choice`（`CombatCard`，`choice_id=0`）
  与 5 条 `action_resumed`；`BRAND` 重放 `choice_id=0`、checksum `5363e882` 复现并合并回 #10。
- 日志无 `[GlobalRewind]` WARN/ERROR；截图确认「结束第 1 回合」按钮、10 张手牌、
  顶栏佩尔之眼、分支图均正常。

## UI 与快捷键

热键不再是硬编码常量：面板右上角「按键」里可以重新绑定，保存到
`%APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg`，重启后继续生效。

| 默认键 | 作用 |
|---|---|
| `F7` | 打开 / 关闭探索树（仅局内有效） |
| `F6` | 回到父节点（撤销一步） |
| `Shift + F6` | 前进到最近子节点 |

| 树中操作 | 作用 |
|---|---|
| 左键编号圆点 | 跳转到该节点 |
| 右键编号圆点 | 剪掉该节点及其整棵子树（当前分支会被拒绝） |
| 悬停编号圆点 | 显示节点详情（类型、回合、校验和、对象/字段数、子节点数） |
| 左键连线 | 折叠该子节点的整棵子树 |
| 左键空心圆 | 展开被折叠的子树；悬停只显示节点数 |
| 拖拽标题栏 | 移动面板（位置会记住） |
| `Ctrl + 滚轮` | 以鼠标为锚缩放（50%–200%） |
| 滚轮 / 横向滚轮 | 纵向 / 横向滚动树 |
| 底部「背景」滑杆 | 面板背景不透明度（20%–100%） |

面板只在游戏内（`RunManager.IsInProgress`）可召出；回到主菜单会自动关闭。右上角不再有常驻文字入口。

## MCP 控制面

模组内置一个轻量 MCP 服务端（JSON-RPC 2.0 over Streamable HTTP），让外部 Agent / 测试脚本
不需要按热键、也不需要任何桥接进程就能读写本模组的能力。它与游戏的通用操作 API 分工明确：
**游戏本身怎么打用游戏侧 MCP（`http://127.0.0.1:8080/mcp`），回溯怎么用用本模组的 MCP。**

- 端点：`http://127.0.0.1:8766/mcp`（`GET /health` 可快速探活）
- 端口覆盖：环境变量 `GLOBAL_REWIND_MCP_PORT`
- 传输：只支持 `POST`（应答 `application/json`），不提供 SSE 下行流
- 线程：网络在后台线程，所有游戏对象访问都排到主线程（`SceneTree.ProcessFrame`）执行

| 工具 | 作用 |
|---|---|
| `rew_status` | 是否在战斗中、当前节点、节点数、是否忙、事件条数、上次恢复判定 |
| `rew_tree` | 导出整棵树（id / parent / 标签 / 回合 / 校验和 / deep hash / 子节点） |
| `rew_capture` | 立刻抓点；状态与当前节点相同则合并（`merged=true`） |
| `rew_jump` | 跳到指定节点：逐字段回读 + 逐字节深比较 + 校验和三重核对，任一不过就回滚。**真联机是异步的**：返回 `pending=true` + `request_id`，结果用 `rew_jump_status` 查 |
| `rew_jump_status` | 协同跳转的进度与结果：阶段、`request_id`、目标节点、各端 ack、失败原因 |
| `rew_mp_status` | 多人协同状态：角色（host/client）、协议版本、对端版本校验、冻结/跳转阶段、待确认锚点、最近一次跳转结果 |
| `rew_jump_parent` / `rew_jump_forward` | 等价 F6 / Shift+F6 |
| `rew_prune` | 剪掉子树（内存与 `nodes/N.json` 一起删）；当前节点在其内时拒绝。**真联机只有房主可调用**，且是协同的：返回 `pending=true`，结果用 `rew_prune_status` 查 |
| `rew_prune_status` | 协同剪枝的进度与结果：阶段、`request_id`、删除数量、剪枝前后树指纹、失败原因 |
| `rew_checksum` | 当前状态的归一化校验和与可读摘要 |
| `rew_state` | 活状态完整快照（JSON）：全部 RNG 计数器、血量/格挡/能量/星/回合/阶段、五个牌堆内容、药水、遗物计数器、球、箭矢、敌人下一步动作与意图文案、`turn_state`（含 `extra_turn`） |
| `rew_node` | 某节点捕获时的摘要（digest / deep hash） |
| `rew_verify` | 当前节点快照 vs 活状态的**逐字段 + 逐字节**核对（刚跳转完应为 0 且 `deep-equal`） |
| `rew_events` | 本局全部战斗事件流水账，支持 `kind` 过滤、`last` 取最后 N 条 |

注意：动作执行中（`busy`）抓点与跳转都会被拒绝——半应用的状态既不可复现也没意义。
`rew_status` 的 `stable` 表示当前是否处于稳定决策点；`rew_capture` 在回合过渡期会返回
`skipped_transition=true` 和具体原因，而不是笼统报错。

测试脚手架（需要启动游戏前设置 `GLOBAL_REWIND_TEST_HARNESS=1`）：`rew_test_fake_mp` /
`rew_test_hold` / `rew_test_release` / `rew_test_status` 用于单进程假多人；
`rew_test_burst` 一次把前 N 张可打出的牌依次入队（走正常 `ActionQueueSynchronizer.RequestEnqueue`，
单机/假多人/真联机都能用），复现“玩家快速连点”；
`rew_test_poison_restore` 让本端下一次协同写回在成功后强制判定失败，用来验证
`Abort + 全员回滚`这整条链路；`rew_test_diff_nodes` 对比两个节点的对象图结构与哈希，用于排查合并问题。

### `rew_events` 记录什么

| kind | 含义 |
|---|---|
| `action_enqueued` | 所有 `GameAction` 入队，**含 hook**（消耗 / 变牌 / 弃牌 / 保留手牌的载体） |
| `action_finished` / `action_cancelled` / `action_resumed` | 动作完成 / 取消 / 玩家选择后恢复 |
| `player_choice` | `PlayerChoiceReceived`：选牌类操作（`PlayerChoiceType` = `CombatCard` / `MutableCard` / `CanonicalCard` / `DeckCard` / `Player` / `Index`） |
| `checksum` | `ChecksumTracker.ChecksumGenerated` |
| `turn_start` / `combat_start` / `combat_end` / `run_started` | 生命周期 |
| `capture` / `capture_merged` / `capture_skipped` | 建点、全同节点合并、回合过渡态被门禁跳过（附原因） |
| `mirror` / `mp_anchor` / `mp_anchor_shared` / `mp_anchor_partial` | 多人锚点：客户端镜像建点、房主广播、全端确认 / 部分端缺失 |
| `mp_hello` / `mp_freeze` / `mp_idle` / `mp_commit` / `mp_restored` / `mp_resume` / `mp_abort` / `mp_rollback` / `mp_current` | 协同跳转协议各阶段与回滚 |
| `mp_prune_prepare` / `mp_prune_ready` / `mp_prune_commit` / `mp_prune` / `mp_prune_abort` | 协同剪枝：准备、各端树指纹确认、提交、完成 / 中止 |
| `jump_request` / `jump` / `jump_rejected`、`prune_request` / `prune` / `prune_rejected` | 回溯与剪枝 |

每条带 `seq / kind / type / detail / action_id / round / turn / node_id`。上限 4096 条，
超出会显式返回 `truncated=true`，不静默丢事件。

## 多人协同跳转（M2）

前提：**所有参与者必须装同一版本的 GlobalRewind**。本模组 `affects_gameplay: true`，
游戏握手会校验 gameplay mod 列表；消息 id 又是按类型名排序生成的，版本不一致时消息 id 会错位。

### 节点编号由房主分配

- 房主建点后广播 `GrAnchorMessage(id, parent, checksum, frontier, label, playable, force)`；
- 客户端在**自己的动作边界**抓一份镜像快照放进有界缓存（按 `checksum + frontier` 命中），
  房主锚点到达时优先取缓存；命中不了才退回“本端此刻也必须静止”的实时抓点，然后回 `GrAnchorAck`。
  这样快速连点时不会因为“锚点到得比下一个动作晚”而退化成 `partial`；
- 房主收齐全员 ack 才把节点标成 `shared`（可跳），否则 `partial`（不可跳，但仍占住编号）；
- 客户端不自己建点、不自增编号；`GrCurrentMessage` 用于“房主合并到旧节点”时同步当前指针。

### 跳转协议（房主权威）

```
房主                                    客户端
 |-- GrFreeze(req, node, checksum) ---->|   禁输入、暂停队列、等本机静止
 |<-- GrIdle(req, ok, checksum, frontier)-|   静止点抓安全快照
 |   （核对每端 checksum/frontier 都等于“当前节点”）
 |-- GrCommit(req, node) --------------->|   写回 + Verify + DeepCompare + 纯哈希
 |<-- GrRestored(req, ok, checksum) ------|
 |   （全员 ok 且 checksum 一致才 Resume）
 |-- GrResume(req, node) --------------->|   各自重建房间、解冻
 |   任一失败：GrAbort(req, reason, rollback) → 已写回的端退回安全快照
```

要点：

- **不进快照的传输态**（TCS、锁、在途队列、pending choice/hook）在冻结前必须为空；写回后显式清空
  `_receivedChoices` / `_hookActions` / `_requestedActionsWaitingForPlayerTurn`，并 `Unpause`。
- **`ChecksumTracker.NextId` 不回滚**：回滚会让 checksum id 与对端错位，触发 `StateDivergence`。
- 只信游戏自己的 `NetFullCombatState` 纯哈希 + `Frontier` 做跨端判据；`deep_hash` 只证明本进程内写回一致。
- `local_scope_hash` 是本进程内的共享字段指纹，双端不一样是正常的。
- 冻结期间用 `_playerActionsDisabled`（手牌/药水/结束回合的权威开关）+ `PauseAllPlayerQueues` 双重挡输入；
  这两个是**本地字段**，恢复后再压一次，Resume 时才放开。

### 剪枝（房主专属）

剪枝不改战斗状态，但两端必须删同一棵子树，否则编号/父子关系会永久错位。流程：

```
房主                                    客户端
 |-- GrPrunePrepare(id, target) ------->|   本地校验：目标存在、非根、当前节点不在子树、
 |                                       |   处于全体静止点、没有未确认锚点
 |<-- GrPruneReady(ok, current, nodes, treeHash)
 |   核对所有端的 current / node_count / treeHash 与房主完全一致
 |-- GrPruneCommit(id, target) -------->|   执行同一次确定性剪枝（内存 + nodes/N.json）
 |<-- GrPruneDone(ok, nodes, treeHash) --|
 |   再核对剪枝后的树指纹
 |   任一端未就绪 / 树不一致 / 超时：GrPruneAbort，所有人都不动树
```

- 只有房主能发起；客户端 `rew_prune` 直接被拒绝（`多人局只有房主可以剪枝`）。
- 必须在全体静止点：`QuiescenceProbe` + 队列为空 + 没有待确认锚点；
  准备到提交之间短暂挡输入、暂停队列并禁止抓点，完成后立即恢复 —— 避免“剪枝过程中又建了一个节点”。
- 树指纹 = `current_id | next_id | (id:parent:checksum)…` 的 FNV-1a 64；准备阶段两端必须完全相同，
  提交后再核一次，出现差异会记 `mp_prune_diverged` 并告警（剪枝已落盘，无法自动回滚）。

## 树的语义

- 每个节点 = 战斗在某个**稳定决策点**的完整状态，编号从 1 递增，剪枝不回收编号。
- **稳定决策点判据**（三个条件同时成立，取自游戏自己的语义）：
  `CombatState.CurrentSide == Player`、`!CombatManager.PlayerActionsDisabled`、
  `PlayerCombatState.Phase == PlayerTurnPhase.Play`。战斗开始（`force`）是唯一例外，用作根节点。
  动作队列瞬时清空**不等于**稳定点：点击结束回合后 `OnEndedTurnLocally()` 立刻置
  `PlayerActionsDisabled=true`、`Phase=End`，直到下一次 `TurnStarted` 才复位，
  所以 `EndPlayerTurnAction` / `ReadyToBeginEnemyTurnAction` / 消耗手牌 hook 完成时
  只会写 `capture_skipped`，不会建节点 —— 单人局点结束回合直接落到下一回合的稳定点。
- 建点时机：战斗开始、玩家回合开始、以及**每个玩家动作的逻辑边界**（`GameAction.Execute` 已结束、
  下一个动作还没开始）。判据不要求动作队列为空：快速连点时下一个动作早已排队，但它属于“未来”，
  不影响当前状态。动画只参与跳转后的画面重建，不参与抓点判定。
  - 派生 hook 还没结算完时，等最后一个 hook 结算再抓（标签仍写父动作，如「打出 STRIKE」）；
  - 结束回合 / 怪物动作不建点：前者是过渡态（`Phase=End`），后者不是玩家决策点；
  - 若边界判据不成立（例如输入已被禁用），退回“等下一个静止点”的延迟抓点兜底。
- 事件入口与官方 `CombatReplayWriter` 一致：`ActionQueueSet.ActionEnqueued`（全部动作含 hook）、
  `ActionQueueSet.ActionResumed`、`PlayerChoiceSynchronizer.PlayerChoiceReceived`、
  `ChecksumTracker.ChecksumGenerated`，以及每个动作的 `AfterFinished` / `BeforeCancelled`。
- 从旧节点继续操作会产生**新分支**，旧分支原样保留；同一父节点可以有多个子节点。
- **合并判据 = checksum + DeepHash**：只看校验和会把「按下结束回合前后」误当成同一状态。
  另外再用一个“合并哈希”（`DeepHash` 去掉 `ActionQueueSet._nextId`）兜底：快速连点时，前一个动作
  结算的瞬间已经给排队中的下一个动作分配了编号，模型其实是同一个状态，不该多出重复节点。
- 快照根额外包含官方 replay 的确定性计数器（`nextActionId` / `nextHookId` / `choiceIds` / `nextChecksumId`）
  与 `CombatTurnState`、`CombatManager._playerActionsDisabled`。这是「跳转 == 重放」的关键：
  不恢复它们，跳回旧节点后新动作的编号会接着当前值递增。
- 恢复失败（逐字段 / 逐字节 / 校验和任一不一致）会自动写回安全快照，不会把游戏留在半恢复状态。

### 恢复时的界面同步

快照恢复是**原地写回模型**，游戏 UI 不会自己更新。跳转后按顺序重建：

1. 换一个新的 `NCombatRoom`（敌人、血条、牌堆计数、能量由游戏自己的初始化逻辑生成）；
2. `NPlayerHand.Add` 逐个补出手牌节点 —— 不补就是**手牌空白、点不了牌**；
3. 按 `Player.PotionSlots` 对齐药水腰带（常驻顶栏，节点按 `(-30,-30)` 摆）；
4. 按 `Monster.NextMove.Intents` 补敌人意图 —— 不补就是**敌人头顶空白**；
5. 补发 `RelicModel.DisplayAmountChanged` 刷新顶栏遗物计数器；
6. `SyncOrbs` 按 `OrbQueue.Capacity` 补槽、按 `Orbs` 顺序塞模型；
7. `ActiveScreenContext.Instance.Update()`；
8. **最后** `SyncEndTurnButton()`：反射调 `OnTurnStarted` / `RefreshEnabled`。
   新房间的按钮初始 Hidden 且停在屏幕外，不发这事件就永远不出现 —— 这是"跳转后结束回合按钮消失"的根因。
   若恢复点已按下结束回合，显示 `UNDO_END_TURN_BUTTON` 并保持可点；额外回合（PaelsEye）由
   `OnTurnStarted` 内部的 `PlayerCanTakeAction` 天然处理。

### 树的布局

布局是**纯计算**（`Presentation/TreeLayout.cs`），不依赖 Godot 节点：

- 第一个子节点的 X 与父节点完全相同（正下方）；其余子节点与它同高，从左往右排；
- 第 i 个子节点放在「第 i-1 个子节点子树最右侧节点」的右边，间距固定（`NodeDiameter + SiblingGap`），
  所以任意两棵子树不会重叠，且能贴多近就多近；
- 被折叠的节点在布局里**等同于叶子**（空心圆），因此折叠/展开会让整棵树重新排版，
  所有受影响节点的位置同步更新；
- 画布尺寸随布局增长，外层 `ScrollContainer` 双向滚动；缩放只是把自然坐标乘上系数并重建，
  不用 `Control.Scale`，避免容器尺寸与输入命中错位。

折叠状态只存在于 UI 层（`HashSet<int>`），不写进 `RewindTree`；子树内部的折叠状态一直保留，
展开外层时里层仍是折叠的。当前节点换了（新抓点 / 跳转）时自动展开它的祖先，保证"当前"始终可见。

## 逐字节等价与全事件记录

跳转流程：`Restore() → Verify() → DeepCompare() → NormalizeAfterRestore() → CombatChecksum`。

- `Verify()` 逐字段回读：引用比引用、标量比值、集合比内容和顺序。
- `DeepCompare()` 把「快照里记的」与「活对象重新读出来的」各做一次固定顺序的规范序列化再逐字节比较；
  浮点按 bit 编码，结构体按字段内容递归，字典/集合按编码排序、列表保留顺序。取 SHA256 前 16 字节当指纹。
  **不一致直接拒绝跳转并回滚**，不会给出一个"看起来恢复了但偏了"的状态。
- `NormalizeAfterRestore()` 只清药水 `IsQueued`，**不重算** `_playerActionsDisabled`（重算会破坏逐字节一致）。

判据：从同一节点重放同一动作 → `action_id` 相同、checksum 相同、DeepHash 相同、合并回原节点。
v0.1.5 / v0.1.6 全部实测通过。

## 构建 / 部署 / 校验

```bash
# 在仓库根目录执行（任意平台；薄壳入口见 build.sh / build.ps1 / build.cmd）
dotnet run --project build --                 # 编译 + 部署
dotnet run --project build -- --no-deploy     # 只编译
dotnet run --project build -- --game "<游戏目录>"   # 手动指定游戏目录

# 静态校验：加载真实 sts2.dll + 已部署的模组 DLL，检查补丁目标是否还在
dotnet run -c Release --project tools/Verify

# 独立 UI 布局随机测试（不需要游戏 / Godot）
dotnet run -c Release --project tools/UiLayoutTest
```

> **部署前必须完全退出游戏**：运行中的进程会锁住 `mods/GlobalRewind/GlobalRewind.dll`，
> `build/` 下的构建器会以退出码 2 结束并提示，不会再"假装成功"。

部署后重启游戏生效。日志在 `%APPDATA%\SlayTheSpire2\logs\godot.log`，过滤 `[GlobalRewind]`。

`bin/`、`obj/` 等中间产物不入库；`dist/` 里的 `<Mod>.dll` + 清单副本是**发布用二进制**，
随仓库提交，每次 Release 构建由 `build/` 自动刷新。编译需要本机的游戏程序集
（`data_sts2_*/sts2.dll`、`GodotSharp.dll`、`0Harmony.dll`），这些 DLL 不随仓库分发。

### UI 布局随机测试

`tools/UiLayoutTest` 直接编译 `Presentation/TreeLayout.cs` 与契约源码（不启动游戏），
用随机 create / prune / collapse / expand 序列跑 5 轮、每轮 100+ 节点（默认 130 起），
逐帧检查：

- 可见节点集合与折叠状态一致；
- 第一子节点与父节点同 X，所有子节点同高、从左到右；
- 相邻兄弟子树 bbox 间距**恰好** = `SiblingGap`（既碰撞不了，也不会留多余空隙）；
- 同层任意两节点圆心距 ≥ 直径 + `SiblingGap`；
- 任意两棵互不包含的子树在 Y 区间相交时 bbox 不重叠；
- 连线不穿过任何节点圆；
- 画布尺寸 = 根子树 bbox + padding（没有额外空白）；
- 同一份数据重复构建坐标完全一致。

默认 5 轮分别覆盖宽树（根挂 100+ 孩子）、深树、平衡树、两种随机树；任一轮失败会在
`tools/UiLayoutTest/out/` 写出操作序列、树结构、布局坐标与错误原因。

落盘目录：`%APPDATA%\SlayTheSpire2\global_rewind\`，整局结束（`RunManager.CleanUp`）时整个删除。
UI 设置单独放在 `%APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg`（透明度 / 缩放 / 面板位置 / 键位），
不随整局清理删除。

## 实机验证清单

1. 完全退出游戏 → `dotnet run --project build --` → 重启游戏 → 主菜单 `act continue_run`（**不要新建局**）。
2. `wait_until_actionable`；日志应出现 `event log subscribed: ActionQueueSet / PlayerChoiceSynchronizer / ChecksumTracker`。
3. `rew_status`：`combat_active=true`、`busy=false`、`stable=true`、`event_count>0`；`rew_events` 有 `combat_start` / `turn_start` / `capture`。
4. 打一张牌 → `rew_events` 应出现 `action_enqueued → checksum → action_finished → capture` 闭环。
5. **快速连发**（`rew_test_burst count=2`）→ 两张牌各自建点，节点链为
   `第 N 回合 → 卡 A → 卡 B`；出牌后立刻结束回合，卡牌节点仍在，之后只多一个「第 N+1 回合」。
6. 结束回合 → 树里只应新增「第 N 回合」一个节点；`rew_events` 里结束回合动作只有一条
   `capture_skipped`（阶段 End），不应出现以该动作命名的节点。
6. `rew_jump` 回旧节点：`verify_mismatches=0`、`deep_equal=true`；`rew_verify` 也应为 0。
7. 重放同一张牌：两次 `action_id` 相同、checksum / deep hash 相同、`capture_merged` 合并回原节点。
8. `rew_state` 跳走再跳回：dict diff = 0。
9. 截图确认「结束第 N 回合」按钮明亮可见、手牌与能量正确。
10. `rew_prune` 当前分支应被拒绝；兄弟分支剪枝后 `node_count` 减少、`nodes/` 无孤儿文件。
11. UI：按 F7 召出面板 → 圆点只显示编号，悬停出详情；点击连线折叠为空心圆并显示节点数；
    点空心圆恢复；折叠里层后再折叠/展开外层，里层状态保持；`Ctrl+滚轮` 缩放、滚轮滚动、拖拽标题、
    调背景滑杆均应生效；回主菜单按热键应只打 `hud open ignored: source unavailable`。
12. **多人 M2**（双实例 ENet，两个都装同版本 mod）：
   - 双端 `rew_mp_status` 的 `role` 分别为 host/client，`peers` 里 `protocol_ok=true`、`mod_version` 相同；
   - 出牌后双端 `rew_tree` 的节点 id / `checksum` / `state=shared` 完全一致；
   - 房主 `rew_jump` → `rew_jump_status` 变 `ok=true, phase=resumed`；双端 `rew_verify` 0 不一致；
   - 客户端 `rew_jump` 同样能触发（由房主执行）；
   - 一方已结束回合时请求跳转应 `aborted` 并给出原因，不应硬跳；
   - `rew_test_poison_restore`（需 `GLOBAL_REWIND_TEST_HARNESS=1`）后跳转应双端回滚、无人被踢；
   - 分支上由房主 `rew_prune`：双端节点数与树指纹一致、被删节点的 `nodes/N.json` 双端都消失；
     客户端 `rew_prune` 应被拒绝；非静止点剪枝应被拒绝。
13. 日志无 `[GlobalRewind] WARN` / ERROR；`tools/Verify` 全绿。

失败时看 `godot.log` 里 `[GlobalRewind]` 行：
`checksum mismatch` 说明恢复后模型与节点记录不一致；
`深拷贝不一致` 说明 `DeepCompare` 抓到了逐字节差异（日志会带 diff）；
`visual rebuild:` 说明模型恢复了但画面重建失败。

## 分层与依赖方向

```
ModEntry（组合根）
  ├─► Patches/   合并后的 Harmony 补丁表 + 补丁体（CleanUp / _Input）
  ├─► App/       RewindRuntime（领域逻辑）、RewindUiSource（逻辑→UI 适配）
  │              RewindHotkeys / RewindInput（输入与热键分发）
  ├─► Net/       M2 协同跳转：mod 消息、网络门面、房主权威状态机
  ├─► Presentation/  纯 UI：契约 + 布局 + Godot 控件
  ├─► Game/      CombatViewRebuilder（游戏房间的 UI 重建，属于游戏集成）
  └─► Mcp/       内置 MCP 服务端
```

- **Presentation 不认识** `RewindRuntime` / `RewindTree` / `Snapshot` / `MegaCrit.*`，
  只依赖 Godot、自己的契约（`ITreeOverlaySource` / `TreeOverlaySnapshot` / `TreeNodeView`）
  与 UI 设置。宿主节点也由外部注入（`Initialize(source, hostProvider)`），
  所以给一份任意树快照它就能独立跑。
- UI 只发 **无语义事件**：`NodeActivated` / `NodeContextActivated` / `HotkeysChanged`；
  "左键=跳转、右键=剪枝" 的映射全部在 `App/RewindUiSource.cs`。
- 逻辑层不引用 UI：状态变化只发 `RewindRuntime.Changed` / `SessionEnded`，
  由 `RewindUiSource` 转发成 UI 契约事件。
- 补丁只剩一个入口 `Patches/ModPatches.cs`：目标表、前缀/后缀、补丁体都在同一处。

## 目录

```
.
├── GlobalRewind.csproj
├── Directory.Build.props          # 编译设置 + 游戏程序集引用（路径由构建器注入）
├── GlobalRewind.json              # 模组清单（affects_gameplay: true）
├── ModEntry.cs                    # 组合根：装配各层
├── Patches/
│   └── ModPatches.cs              # 合并后的补丁表 + 补丁体
├── App/
│   ├── RewindRuntime.cs           # 全事件订阅 / 抓点 / 恢复 / 剪枝 / 生命周期
│   ├── RewindUiSource.cs          # 逻辑 → UI 适配器（唯一翻译层）
│   ├── RewindHotkeys.cs           # 可配置热键分发 + 轮询兜底 + 去重
│   └── RewindInput.cs             # 常驻输入节点 + ProcessFrame 轮询
├── Presentation/
│   ├── TreeOverlayContracts.cs    # ITreeOverlaySource + 快照 DTO
│   ├── TreeLayout.cs              # 纯计算的树形布局（折叠视作叶子）
│   ├── RewindGraphView.cs         # 圆点 / 连线 / 空心圆绘制与点击
│   ├── RewindHud.cs               # 浮层：拖拽 / 滚动 / 缩放 / 设置面板
│   └── RewindSettings.cs          # ui.cfg：透明度、缩放、位置、键位
├── Game/
│   └── CombatViewRebuilder.cs     # 恢复后整房间重建（含结束回合按钮 / 充能球）
├── Net/
│   ├── RewindMessages.cs          # M2 mod 消息（跳转 + 剪枝，共 17 个 INetMessage）
│   ├── RewindNetChannel.cs        # INetGameService 门面：注册/广播/单发/peer 列表
│   └── RewindCoordinator.cs       # 房主权威状态机：锚点镜像 + 协同跳转 + 回滚
├── Mcp/
│   └── RewindMcpServer.cs         # 内置 MCP 服务端（127.0.0.1:8766/mcp）
├── Snapshot/
│   ├── MementoRules.cs            # 可进快照的类型判定 + 按类型字段白名单
│   ├── ObjectGraphSnapshot.cs     # 原地 memento：捕获/恢复/Verify/DeepCompare
│   ├── CombatChecksum.cs          # 用 NetFullCombatState 校验和当判据
│   ├── Frontier.cs                # 多人时间坐标（计数器/阶段/就绪集合）
│   ├── QuiescenceProbe.cs         # 全体静止点探针
│   ├── SnapshotScope.cs           # Shared / Local / Excluded 与快照根
│   ├── RestoreOutcome.cs          # 写回结果（SP 与 MP 共用）
│   ├── LiveStateProbe.cs          # 活状态 JSON（rew_state）
│   └── CombatEventLog.cs          # 全事件流水账（rew_events）
├── Players/
│   ├── PlayerScope.cs             # 本地/全体/活跃玩家（不再写 Players[0]）
│   └── TurnStateProbe.cs          # CombatTurnState 反射读取
├── Tree/
│   ├── RewindTree.cs              # 节点、分支、编号、剪枝、AddWithId 镜像
│   └── RewindStore.cs             # 落盘（tree.json + 节点摘要）
├── build/                         # 跨平台构建器（探测游戏目录 → 编译 → 部署）
│   ├── Build.csproj
│   └── Program.cs
├── build.sh / build.ps1 / build.cmd   # 薄壳入口
├── dist/                          # 发布用二进制（<Mod>.dll + 清单副本），随仓库提交
├── tools/
│   ├── Verify/                    # 静态校验：补丁目标 / 字段 / 成员是否还在
│   └── UiLayoutTest/              # 树布局随机测试（不启动游戏）
├── docs/
│   ├── combat-rewind-research.md  # 同类模组调研
│   ├── dag-visualization.md       # DAG / 传送门 / 连线走向的设计与迭代
│   ├── design.md                  # 设计、状态集、落盘格式、已知限制
│   ├── multiplayer-plan.md        # 多人 M0–M4 方案与实测记录
│   └── troubleshooting.md         # 启动失败/跳转失败的判读方法
├── verify/targets.json            # 给 tools/Verify 的目标清单
└── README.md
```

设计细节与限制见 [docs/design.md](docs/design.md)。
