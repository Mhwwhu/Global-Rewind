# 故障排查

日志：`%APPDATA%\SlayTheSpire2\logs\godot.log`（每次启动会滚动成 `godot<时间>.log`）。
本模组的行都以 `[GlobalRewind]` 开头。

## 1. 先分清"谁失败了"

模组加载器按顺序输出，失败点一定在 `Failed ✗` / `Exception` / `blocked` 附近：

| 日志特征 | 含义 |
|---|---|
| `Found mod manifest file ...\GlobalRewind\GlobalRewind.json` | 清单被发现 |
| `Loading assembly DLL ...\GlobalRewind.dll` | 程序集已加载 |
| `Calling initializer method of type GlobalRewind.ModEntry` | 入口被调用 |
| `[GlobalRewind] loaded on game vX.Y.Z` | 本模组初始化成功 |
| `Finished mod initialization for 'Global Rewind ...'` | 本模组加载完成 |
| `[ERROR] ... [Critical] ... - Failed ✗` + `mod loading blocked` | **另一个模组**的关键补丁失败 |
| `Exception thrown when calling mod initializer of type X` | X 的初始化抛异常 |

只要出现 `[GlobalRewind] loaded on game ...` 和 `Finished mod initialization for 'Global Rewind ...'`，
就说明本模组**已经加载成功**；此时弹出的"模组启动失败"对话框指的是别的模组。

## 2. 已遇到的案例：RitsuLib 0.6.15 与游戏 v0.114.0 不兼容

现象（2026-11-26，游戏从 0.113.1 更新到 0.114.0 后）：

```
[ERROR] [com.ritsukage.sts2-RitsuLib] [Critical] ritsulib_card_capability_downgrade_lifecycle - Failed ✗
[Patcher - framework core]   Error: Target method not found: CardModel.DowngradeInternal
[ERROR] [com.ritsukage.sts2-RitsuLib] [Critical] ritsulib_card_capability_upgrade_lifecycle - Failed ✗
[Patcher - framework core]   Error: Target method not found: CardModel.UpgradeInternal
[ERROR] [com.ritsukage.sts2-RitsuLib] 2 critical patch(es) failed, mod loading blocked
[ERROR] [com.ritsukage.sts2-RitsuLib] Critical patch(es) failed, rolling back all patches...
[ERROR] Exception thrown when calling mod initializer of type STS2RitsuLib.Loader.Bootstrap
```

原因：v0.114.0 移除了 `CardModel.UpgradeInternal` / `DowngradeInternal`（或改了签名），
RitsuLib 0.6.15 声明 `compat branch: 0.113.0`，两条 critical 补丁挂不上就整体回滚并拒绝初始化。

同一个日志里本模组是正常的：

```
[GlobalRewind] patches applied: 2/2
[GlobalRewind] loaded on game v0.114.0
[GlobalRewind] settings loaded: alpha=0.93 zoom=1.00 keys=F7/F6/Shift+F6
Finished mod initialization for 'Global Rewind - 战斗内探索树' (GlobalRewind).
```

处置：

1. **优先更新 RitsuLib**（创意工坊 3747602295）。它的发布包含多分支 variant pack，选择一个含 0.114 API 的版本。
2. 如果暂时没有更新，可以停用 RitsuLib。注意 `奶蛙 Naiwa` 的程序集引用了
   `RitsuLib.ModSettingsInterop`，停用后它的设置项可能不可用（纯外观模组，通常仍能跑）。
3. 本模组不依赖 RitsuLib，也不依赖 BaseLib，停用它不影响 GlobalRewind。

## 3. 游戏更新后的自检流程

```bash
# 1) 对照当前游戏程序集检查补丁目标是否还在
dotnet run -c Release --project tools/Verify
# 输出 ALL CHECKS PASSED 才说明 2 个补丁 + 反射目标都还在

# 2) 重新编译（引用的是 <游戏>/data_sts2_*/sts2.dll，脚本会自动探测平台目录）
dotnet run --project build --

# 3) 重启游戏，看日志首行版本号与本模组加载行
```

## 4. 本模组自己的失败信号

进入战斗后如果出问题，按关键字定位：

| 关键字 | 含义 | 处理 |
|---|---|---|
| `capture failed` | 抓快照时反射/内存异常 | 把上下文发出来；通常是某个新模型字段触发了不支持的类型 |
| `checksum mismatch`（日志里是 `jump #N rejected`） | 恢复后模型与节点记录不一致 | 说明还有状态没被 memento 覆盖，带上期望/实际 checksum |
| `visual rebuild:` | 模型恢复成功但画面重建失败 | 模型正确、画面可能停在旧状态；记录错误文本 |
| `node #N ... (0 objects)` | 快照为空 | 根对象取错或战斗尚未建立，检查是否在 `CombatBegan` 之前触发 |
| 完全没有 `[GlobalRewind]` | 模组未加载或被禁用 | 查 `mods/GlobalRewind/GlobalRewind.json` 与设置里的禁用列表 |
| 有 `node #N` 但按热键无反应 | 不在局内（主菜单会打 `hud open ignored: source unavailable`）、键位被改过，或 HUD 创建失败 | 看 `hud opened`；两条输入通路都应出现 `input: toggle hud via harmony/poll` |
| 面板出现但树是空的 | 本场战斗还没建点，或刚进战斗还没到稳定决策点 | `rew_status.node_count` 应 ≥ 1 |

### 输入的三条通路

- `NGame._Input` 的 Harmony postfix（`via harmony`）
- 挂在 `NGame` 下的常驻 `RewindInputNode`（`via node` / `via unhandled`），`_Ready` 里显式 `SetProcessInput(true)`；
- `SceneTree.ProcessFrame` 里按当前绑定键轮询（`via poll`）。

三路共用一个分发器（`App/RewindHotkeys.cs`），150ms 内同一动作去重，不会出现按一次开两次。
键位来自 `%APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg`，可在面板「按键」里改；
如果改完忘了键位，删掉这个文件重启即可回到 F7 / F6 / Shift+F6（或直接编辑 `key_toggle=` 一行）。

## 5. 部署"看起来成功"，但游戏里还是旧版本

**游戏运行时 `<游戏>/mods/GlobalRewind/GlobalRewind.dll` 被进程锁住，复制会失败。**
如果构建脚本没有检查复制结果，就会打印"部署完成"但实际文件没变，于是反复重启也看不到新行为。

自查：

```bash
md5sum bin/Release/GlobalRewind.dll \
       "<游戏>/mods/GlobalRewind/GlobalRewind.dll"
```

两个哈希不一致，或复制时报 `Device or resource busy`，就是这个问题。`build/` 下的构建器已改为
复制失败时直接以退出码 2 结束并提示"请完全退出 SlayTheSpire2.exe"。

## 6. 已知的"不是 bug"

- 敌人回合按 F6/点击节点会被拒绝（`有动作正在执行` 或 `不在战斗中`）：跳转要求动作队列空闲。
- 当前节点所在子树不能剪枝：先跳到别的分支，否则"显示的当前节点"和实际状态会不一致。
- 事件战斗里画面可能重建不完整：v0.1 未区分嵌入布局，见 `docs/design.md` §8。
- 主菜单 / 大厅里没有任何节点是正常的：本模组的节点只在战斗内产生。
- 联机时直接停用（`CombatBegan skipped: not singleplayer`），不会产生任何节点。

## 7. 创意工坊模组"无法更新 / 页面打不开"

页面打不开**不等于**条目下架。Steam 社区前端对个别条目会直接返回
`Sorry! An error was encountered ... There was a problem accessing the item`，而 Web API 仍然正常。

判断条目真实状态（必须用 POST）：

```bash
curl -s -X POST "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/" \
  -d "itemcount=1" -d "publishedfileids[0]=<工坊ID>"
```

关注 `visibility`（0=公开）、`banned`、`time_updated` 三个字段。

再对比本地客户端缓存：`steamapps/workshop/appworkshop_2868840.acf` 里该 ID 的
`timeupdated` 与 `latest_timeupdated`。两者相等、但小于 API 的 `time_updated`，
说明**客户端没拉到新 manifest**，不是作者没更新。

### 本次案例（RitsuLib，工坊 3747602295）

| 来源 | 值 |
|---|---|
| Steam API | `visibility=0`、`banned=0`、`time_updated = 2026-11-25 02:35 UTC`（对应 0.6.16） |
| 本地 ACF | `timeupdated = latest_timeupdated = 2026-11-20 15:23`（0.6.15） |
| 本地 `mod_manifest.json` | `version: 0.6.15`，variants 仅含 0.107.1 / 0.111.0 / 0.113.0 |

### 修复顺序

1. **完全退出 Steam**（托盘 → 退出），重新启动，再启动游戏。多数情况到这一步就会重新拉 manifest。
2. 仍不行：Steam 库 → 右键游戏 → 创意工坊 → 取消订阅 RitsuLib → 重启 Steam → 重新订阅（强制重新解析最新清单）。
3. 仍不行：Steam → 设置 → 下载 → 清除下载缓存（需重新登录）。
4. 兜底手动安装：下 `STS2-RitsuLib.0.6.16.variant-pack.zip`
   （<https://github.com/BAKAOLC/STS2-RitsuLib/releases/tag/v0.6.16>），解压为
   `<游戏>/mods/STS2-RitsuLib/`，保留根 DLL、`shared/`、`compat/`、`assets.zip` 与两个 manifest；
   同时取消工坊订阅并删掉 `steamapps/workshop/content/2868840/3747602295`，避免同 id 加载两份。

更新成功的标志：日志里 `RitsuLib Version: 0.6.16`，`ritsulib-variants.manifest` 出现 0.114 目标，
且不再出现 `Target method not found: CardModel.UpgradeInternal`。
