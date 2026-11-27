using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.App;
using GlobalRewind.Game;
using GlobalRewind.Logging;
using GlobalRewind.Net;
using GlobalRewind.Players;
using GlobalRewind.Snapshot;
using GlobalRewind.Tree;

namespace GlobalRewind.Mcp;

/// <summary>
/// GlobalRewind 自带的轻量 MCP 服务端（JSON-RPC 2.0 over Streamable HTTP）。
///
/// 为什么在 mod 里跑：
///   · 跳转 / 剪枝 / 抓点是本模组的内部能力，游戏的通用 API 没有入口；热键只能靠人按。
///   · 放在同进程里就能直接调 <see cref="RewindRuntime"/>，不需要任何外部桥接进程或文件协议。
///
/// 传输：`POST http://127.0.0.1:8766/mcp`，应答用 `application/json`（不提供 SSE 推送）。
/// 线程：监听与解析在后台线程；所有游戏对象访问都排到主线程（SceneTree.ProcessFrame）执行。
/// 端口：环境变量 `GLOBAL_REWIND_MCP_PORT` 可覆盖，默认 8766。
/// </summary>
internal static class RewindMcpServer
{
    public const int DefaultPort = 8766;
    private const string ProtocolVersion = "2024-11-05";
    private const int ToolTimeoutMs = 30000;

    private static readonly ConcurrentQueue<WorkItem> Queue = new ConcurrentQueue<WorkItem>();

    private static volatile int _port = DefaultPort;
    private static bool _started;
    private static string _sessionId = "gr-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public static int Port => _port;

    private sealed class WorkItem
    {
        public Func<string> Action;
        public string Result;
        public Exception Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    // ---------------------------------------------------------------- 生命周期

    public static void Install()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _port = ResolvePort();

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += Pump;
        }
        else
        {
            ModLog.Warn("mcp: no SceneTree, tool calls will time out");
        }

        var thread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "GlobalRewindMcp"
        };
        thread.Start();
    }

    private static int ResolvePort()
    {
        try
        {
            string raw = System.Environment.GetEnvironmentVariable("GLOBAL_REWIND_MCP_PORT");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int port)
                && port > 1024 && port < 65536)
            {
                return port;
            }
        }
        catch
        {
            // 读环境变量失败就用默认端口
        }

        return DefaultPort;
    }

    /// <summary>主线程：把排队的工具调用逐个执行并回填结果。</summary>
    private static void Pump()
    {
        int budget = 8;
        while (budget-- > 0 && Queue.TryDequeue(out WorkItem item))
        {
            try
            {
                item.Result = item.Action();
            }
            catch (Exception e)
            {
                item.Error = e;
            }
            finally
            {
                item.Done.Set();
            }
        }
    }

    private static string RunOnMainThread(Func<string> action)
    {
        var item = new WorkItem { Action = action };
        Queue.Enqueue(item);

        if (!item.Done.Wait(ToolTimeoutMs))
        {
            throw new TimeoutException("main thread did not pick up the call in time");
        }

        if (item.Error != null)
        {
            throw item.Error;
        }

        return item.Result;
    }

    // ---------------------------------------------------------------- 传输层

    private static void AcceptLoop()
    {
        TcpListener listener = null;

        // 双开（AI 队友 / 手动 --fastmp）时两个实例都会继承同一个环境变量，
        // 默认端口必然冲突。这里从配置端口开始向后找 10 个可用端口，
        // 实际端口写回 _port，/health 与 rew_status 都会回显。
        int startPort = _port;
        for (int offset = 0; offset < 10; offset++)
        {
            int candidate = startPort + offset;
            if (candidate > 65535)
            {
                break;
            }

            try
            {
                var probe = new TcpListener(IPAddress.Loopback, candidate);
                probe.Start();
                listener = probe;
                _port = candidate;
                break;
            }
            catch (Exception e)
            {
                ModLog.Warn($"mcp port {candidate} unavailable: {e.GetType().Name} {e.Message}");
            }
        }

        if (listener == null)
        {
            ModLog.Error($"mcp listen failed: no free port in {startPort}..{startPort + 9}");
            return;
        }

        ModLog.Info($"mcp listening on http://127.0.0.1:{_port}/mcp");

        while (true)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (Exception e)
            {
                ModLog.Warn($"mcp accept failed: {e.Message}");
                Thread.Sleep(200);
            }
        }
    }

    private static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 10000;
                NetworkStream stream = client.GetStream();

                if (!TryReadRequest(stream, out string method, out string path, out string body))
                {
                    return;
                }

                if (method == "POST")
                {
                    string response = HandleJsonRpc(body);
                    WriteResponse(stream, 200, "application/json", response);
                }
                else if (method == "GET" && path.StartsWith("/health"))
                {
                    bool combat = false;
                    int nodes = 0;
                    try
                    {
                        combat = RewindRuntime.Instance.IsCombatActive;
                        nodes = RewindRuntime.Instance.Tree.Count;
                    }
                    catch
                    {
                        // 主线程还没就绪时不阻塞探活
                    }

                    WriteResponse(stream, 200, "application/json",
                        "{\"ok\":true,\"mod\":\"GlobalRewind\",\"port\":" + _port
                        + ",\"combat_active\":" + (combat ? "true" : "false")
                        + ",\"node_count\":" + nodes + "}");
                }
                else if (method == "GET")
                {
                    // 本服务不提供 SSE 下行流：明确告知，客户端应改用 POST。
                    WriteResponse(stream, 405, "application/json",
                        "{\"error\":\"only POST /mcp is supported (no server-initiated SSE stream)\"}");
                }
                else if (method == "DELETE")
                {
                    WriteResponse(stream, 200, "application/json", "{}");
                }
                else
                {
                    WriteResponse(stream, 405, "application/json", "{\"error\":\"method not allowed\"}");
                }
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"mcp connection failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static bool TryReadRequest(NetworkStream stream, out string method, out string path, out string body)
    {
        method = null;
        path = null;
        body = null;

        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                return false;
            }

            buffer.Write(chunk, 0, read);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        byte[] all = buffer.GetBuffer();
        string header = Encoding.UTF8.GetString(all, 0, headerEnd);
        string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return false;
        }

        method = requestLine[0].ToUpperInvariant();
        path = requestLine[1];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        int available = (int)buffer.Length - bodyStart;
        var payload = new MemoryStream();
        if (available > 0)
        {
            payload.Write(all, bodyStart, available);
        }

        while (payload.Length < contentLength)
        {
            int read = stream.Read(chunk, 0, Math.Min(chunk.Length, contentLength - (int)payload.Length));
            if (read <= 0)
            {
                break;
            }

            payload.Write(chunk, 0, read);
        }

        body = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
        return true;
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private static void WriteResponse(NetworkStream stream, int status, string contentType, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        string reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 405 ? "Method Not Allowed" : "Error";

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        head.Append("Mcp-Session-Id: ").Append(_sessionId).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (bytes.Length > 0)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.Flush();
    }

    // ---------------------------------------------------------------- JSON-RPC

    private static string HandleJsonRpc(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (Exception e)
        {
            return ErrorResponse(null, -32700, "parse error: " + e.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out JsonElement methodElement))
            {
                // 客户端可能发 notification 之外的空包，这里回空对象避免连接层报错
                return "{}";
            }

            string method = methodElement.GetString() ?? string.Empty;
            string id = null;
            if (root.TryGetProperty("id", out JsonElement idElement))
            {
                id = idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetRawText()
                    : idElement.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(idElement.GetString()) : null;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        return ResultResponse(id, InitializeResult());
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return "{}";
                    case "ping":
                        return ResultResponse(id, "{}");
                    case "tools/list":
                        return ResultResponse(id, ToolsListResult());
                    case "tools/call":
                        return ResultResponse(id, CallTool(root));
                    case "resources/list":
                        return ResultResponse(id, "{\"resources\":[]}");
                    case "prompts/list":
                        return ResultResponse(id, "{\"prompts\":[]}");
                    default:
                        return ErrorResponse(id, -32601, "method not found: " + method);
                }
            }
            catch (Exception e)
            {
                ModLog.Warn($"mcp {method} failed: {e.GetType().Name} {e.Message}");
                return ErrorResponse(id, -32603, e.Message);
            }
        }
    }

    private static string InitializeResult()
    {
        return "{\"protocolVersion\":" + JsonSerializer.Serialize(ProtocolVersion)
               + ",\"capabilities\":{\"tools\":{},\"resources\":{}}"
               + ",\"serverInfo\":{\"name\":\"global-rewind\",\"version\":\""
               + typeof(RewindMcpServer).Assembly.GetName().Version + "\"}}";
    }

    private static string ResultResponse(string id, string resultJson)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"result\":" + resultJson + "}";
    }

    private static string ErrorResponse(string id, int code, string message)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null")
               + ",\"error\":{\"code\":" + code + ",\"message\":" + JsonSerializer.Serialize(message ?? "error") + "}}";
    }

    // ---------------------------------------------------------------- 工具定义

    private sealed class ToolSpec
    {
        public string Name;
        public string Description;
        public string Schema;
    }

    private static readonly ToolSpec[] Tools =
    {
        new ToolSpec
        {
            Name = "rew_status",
            Description = "GlobalRewind 运行状态：是否在战斗中、当前节点、节点数、是否有动作在执行、"
                          + "单人/联机/假多人、每个玩家的 phase/energy/ready、全体静止点与当前切面。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_tree",
            Description = "导出当前战斗的探索树：所有节点的 id/父节点/标签/类型/回合/校验和/子节点，以及当前所在节点。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_capture",
            Description = "立刻在当前游戏状态抓一个节点（相当于手动的快照点）。如果与当前节点状态完全相同会被合并，返回 merged=true。",
            Schema = "{\"type\":\"object\",\"properties\":{\"label\":{\"type\":\"string\",\"description\":\"节点标签，默认『手动抓点』\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_jump",
            Description = "跳转到指定节点：恢复该节点的完整战斗状态，并用校验和核对。失败时返回可读错误"
                          + "（不在战斗中/节点不存在/校验不一致）。真联机局是**异步**的协同跳转："
                          + "返回 pending=true 和 request_id，用 rew_jump_status 查询最终结果；"
                          + "房主与客户端都可以发起，实际由房主统一广播 Freeze/Commit/Resume。",
            Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\",\"description\":\"目标节点 id\"}},\"required\":[\"id\"]}"
        },
        new ToolSpec
        {
            Name = "rew_jump_status",
            Description = "协同跳转的进度与结果：阶段（Freezing/Idle/WaitRestored/resumed/aborted）、"
                          + "request_id、目标节点、是否成功、失败原因、各端 ack。单机局返回当前节点摘要。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_mp_status",
            Description = "多人协同状态：角色（host/client）、协议版本、对端列表与版本校验、"
                          + "冻结/跳转阶段、待确认锚点、最近一次跳转结果。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_jump_parent",
            Description = "跳回当前节点的父节点（等价于 F6）。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_jump_forward",
            Description = "前进到当前节点的第一个子节点（等价于 Shift+F6）。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_prune",
            Description = "剪枝：删掉指定节点及其所有后代。当前节点在该子树内时会被拒绝（返回 error）。"
                          + "真联机是**房主专属**的协同剪枝：客户端调用直接拒绝；房主调用返回 pending=true，"
                          + "由 Prepare → 全端 ready（树指纹一致）→ Commit 完成，结果用 rew_prune_status 查询。",
            Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\",\"description\":\"要剪掉的子树根 id\"}},\"required\":[\"id\"]}"
        },
        new ToolSpec
        {
            Name = "rew_prune_status",
            Description = "协同剪枝的进度与结果：阶段（prepare/commit/done/aborted）、请求号、目标节点、"
                          + "删除数量、剪枝后树指纹、失败原因。单机局返回最近一次本地剪枝结果。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_checksum",
            Description = "当前战斗状态的归一化校验和与可读摘要，用于跨操作比对状态是否一致。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_frontier",
            Description = "当前逻辑切面（多人时间坐标）：动作 id、hook id、每个座位的 choice 计数器、回合/当前侧、"
                          + "每个玩家的回合号与阶段、谁已结束回合/额外回合，以及“全体静止点”探测结果。"
                          + "多人局用它判断两个端是不是同一个逻辑点。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_state",
            Description = "当前活状态的完整快照（JSON）：run RNG 每个类型的 counter/状态字、玩家 RNG、血量/格挡/能量/星/回合/阶段、"
                          + "五个牌堆内容、药水、遗物与计数器、球、箭矢、敌人血量/格挡/Power/下一步动作/意图文案。"
                          + "跳转前后各取一次可直接逐字比对。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_node",
            Description = "某个节点捕获时记录的状态摘要（digest）：跳转后可用它和 rew_state 逐行比对，确认恢复后的状态与捕获时完全一致。",
            Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\",\"description\":\"节点 id\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_verify",
            Description = "把当前节点的内存快照和活状态逐字段核对（引用/标量/集合内容），返回不一致的字段列表。"
                          + "刚跳转完调用应返回 0 个不一致；之后继续操作会有差异，属正常。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_events",
            Description = "导出本局战斗内全部事件的流水账（按发生顺序）：动作入队（含 hook）、完成/取消/恢复、"
                          + "玩家选择（消耗/变牌/弃牌/保留手牌等）、校验和生成、回合开始、抓点与全同合并、"
                          + "跳转与拒绝、剪枝。可用 kind / actor / stream 过滤、last 只取最后 N 条，用于证明没有事件被漏记。",
            Schema = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"description\":\"只返回该 kind 的事件，例如 player_choice / action_enqueued / jump\"},\"last\":{\"type\":\"number\",\"description\":\"只返回最后 N 条，0 或省略表示全部\"},\"actor\":{\"type\":\"number\",\"description\":\"只返回该玩家 NetId 产生的事件\"},\"stream\":{\"type\":\"string\",\"description\":\"只返回该流的事件：action / hook / choice / turn / checksum / system\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_test_fake_mp",
            Description = "[测试脚手架] 起一局 N 人“假多人”（单进程 dummy player），本地玩家故意放在最后一个座位。"
                          + "需要启动游戏前设置 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{\"players\":{\"type\":\"number\",\"description\":\"2~4，默认 2\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_test_hold",
            Description = "[测试脚手架] 给指定玩家入队一个可控 no-op 动作，让它停在队列里；用于验证“有人在途 → 不抓点”。",
            Schema = "{\"type\":\"object\",\"properties\":{\"net_id\":{\"type\":\"number\",\"description\":\"玩家 NetId；0 表示本地玩家\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_test_release",
            Description = "[测试脚手架] 放行由 rew_test_hold 入队的动作。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_test_status",
            Description = "[测试脚手架] 查看假多人 harness 的开关/启动/占用状态。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_test_poison_restore",
            Description = "[测试脚手架] 让本端下一次协同写回在“成功”之后立即判定失败，"
                          + "用于验证 Abort + 全员回滚链路。需要 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{}}"
        },
        new ToolSpec
        {
            Name = "rew_test_burst",
            Description = "[测试脚手架] 一次性把前 N 张可打出的牌依次入队（模拟玩家快速连点，绕过游戏侧 API 的 409），"
                          + "用于验证“动作边界抓点”是否每张牌都建点。走正常的 ActionQueueSynchronizer 入队路径，"
                          + "单机/假多人/真联机都能用。需要 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"number\",\"description\":\"连发张数，默认 2，最多 4\"}}}"
        },
        new ToolSpec
        {
            Name = "rew_test_portal",
            Description = "[测试脚手架] 在 parent 下造一个指向 target 的传送门（不抓快照），用于验证同层走线与剪枝重选。"
                          + "需要 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{\"parent\":{\"type\":\"number\"},\"target\":{\"type\":\"number\"}},\"required\":[\"parent\",\"target\"]}"
        },
        new ToolSpec
        {
            Name = "rew_test_shrink_depth",
            Description = "[测试脚手架] 把节点上移 N 层（改挂到祖父下），制造“更深的同态节点”以验证换主。"
                          + "需要 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\"},\"levels\":{\"type\":\"number\",\"description\":\"默认 1\"}},\"required\":[\"id\"]}"
        },
        new ToolSpec
        {
            Name = "rew_test_diff_nodes",
            Description = "[测试脚手架] 对比两个节点的对象图结构（按类型统计 + 首个差异行），"
                          + "用于排查“同一模型状态为什么没有合并”。需要 GLOBAL_REWIND_TEST_HARNESS=1。",
            Schema = "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}},\"required\":[\"a\",\"b\"]}"
        }
    };

    private static string ToolsListResult()
    {
        var sb = new StringBuilder();
        sb.Append("{\"tools\":[");
        for (int i = 0; i < Tools.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(Tools[i].Name));
            sb.Append(",\"description\":").Append(JsonSerializer.Serialize(Tools[i].Description));
            sb.Append(",\"inputSchema\":").Append(Tools[i].Schema).Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string CallTool(JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("name", out JsonElement nameElement))
        {
            return ToolError("missing params.name");
        }

        string name = nameElement.GetString() ?? string.Empty;
        JsonElement arguments = default;
        if (parameters.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
        {
            arguments = args;
        }

        string payload;
        switch (name)
        {
            case "rew_status":
                payload = RunOnMainThread(ToolStatus);
                break;
            case "rew_tree":
                payload = RunOnMainThread(ToolTree);
                break;
            case "rew_capture":
                payload = RunOnMainThread(() => ToolCapture(GetString(arguments, "label") ?? "手动抓点"));
                break;
            case "rew_jump":
                payload = RunOnMainThread(() => ToolJump(GetInt(arguments, "id")));
                break;
            case "rew_jump_parent":
                payload = RunOnMainThread(ToolJumpParent);
                break;
            case "rew_jump_status":
                payload = RunOnMainThread(ToolJumpStatus);
                break;
            case "rew_mp_status":
                payload = RunOnMainThread(ToolMpStatus);
                break;
            case "rew_jump_forward":
                payload = RunOnMainThread(ToolJumpForward);
                break;
            case "rew_prune":
                payload = RunOnMainThread(() => ToolPrune(GetInt(arguments, "id")));
                break;
            case "rew_prune_status":
                payload = RunOnMainThread(ToolPruneStatus);
                break;
            case "rew_checksum":
                payload = RunOnMainThread(ToolChecksum);
                break;
            case "rew_frontier":
                payload = RunOnMainThread(ToolFrontier);
                break;
            case "rew_state":
                payload = RunOnMainThread(ToolState);
                break;
            case "rew_node":
                payload = RunOnMainThread(() => ToolNode(GetInt(arguments, "id")));
                break;
            case "rew_verify":
                payload = RunOnMainThread(ToolVerify);
                break;
            case "rew_events":
                payload = RunOnMainThread(() => ToolEvents(
                    GetInt(arguments, "last"),
                    GetString(arguments, "kind"),
                    GetULong(arguments, "actor"),
                    GetString(arguments, "stream")));
                break;
            case "rew_test_fake_mp":
                payload = RunOnMainThread(() =>
                {
                    int count = GetInt(arguments, "players");
                    return FakeMultiplayerHarness.Start(count > 0 ? count : 2);
                });
                break;
            case "rew_test_hold":
                payload = RunOnMainThread(() => FakeMultiplayerHarness.Hold(GetULong(arguments, "net_id")));
                break;
            case "rew_test_release":
                payload = RunOnMainThread(FakeMultiplayerHarness.Release);
                break;
            case "rew_test_status":
                payload = RunOnMainThread(FakeMultiplayerHarness.Status);
                break;
            case "rew_test_poison_restore":
                payload = RunOnMainThread(ToolPoisonRestore);
                break;
            case "rew_test_burst":
                payload = RunOnMainThread(() => ToolBurst(GetInt(arguments, "count")));
                break;
            case "rew_test_portal":
                payload = RunOnMainThread(() =>
                {
                    if (System.Environment.GetEnvironmentVariable("GLOBAL_REWIND_TEST_HARNESS") != "1")
                    {
                        return JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game"
                        });
                    }

                    bool ok = RewindRuntime.Instance.AddTestPortal(
                        GetInt(arguments, "parent"),
                        GetInt(arguments, "target"),
                        out int portalId,
                        out string portalError);
                    return JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["ok"] = ok,
                        ["error"] = portalError,
                        ["portal_id"] = portalId,
                        ["current_id"] = RewindRuntime.Instance.Tree.CurrentId
                    });
                });
                break;
            case "rew_test_shrink_depth":
                payload = RunOnMainThread(() =>
                {
                    if (System.Environment.GetEnvironmentVariable("GLOBAL_REWIND_TEST_HARNESS") != "1")
                    {
                        return JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game"
                        });
                    }

                    int levels = GetInt(arguments, "levels");
                    if (levels <= 0)
                    {
                        levels = 1;
                    }

                    bool ok = RewindRuntime.Instance.ShrinkDepth(
                        GetInt(arguments, "id"),
                        levels,
                        out int oldDepth,
                        out int newDepth,
                        out string error);
                    return JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["ok"] = ok,
                        ["error"] = error,
                        ["old_depth"] = oldDepth,
                        ["new_depth"] = newDepth,
                        ["current_id"] = RewindRuntime.Instance.Tree.CurrentId,
                        ["fingerprint"] = RewindRuntime.Instance.TreeFingerprint()
                    });
                });
                break;
            case "rew_test_diff_nodes":
                payload = RunOnMainThread(() => ToolDiffNodes(GetInt(arguments, "a"), GetInt(arguments, "b")));
                break;
            default:
                return ToolError("unknown tool: " + name);
        }

        return "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(payload) + "}],\"isError\":false}";
    }

    private static string ToolError(string message)
    {
        return "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(message) + "}],\"isError\":true}";
    }

    private static string GetString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int GetInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
            {
                return parsed;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- 工具实现（主线程）

    private static string ToolStatus()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        var players = new List<object>(4);

        foreach (var player in PlayerScope.All)
        {
            if (player == null)
            {
                continue;
            }

            players.Add(new Dictionary<string, object>
            {
                ["net_id"] = player.NetId,
                ["slot"] = PlayerScope.SlotOf(player),
                ["local"] = PlayerScope.IsLocal(player),
                ["character"] = player.Character?.Id.Entry,
                ["dead"] = player.Creature?.IsDead ?? false,
                ["phase"] = player.PlayerCombatState?.Phase.ToString(),
                ["turn"] = player.PlayerCombatState?.TurnNumber ?? 0,
                ["energy"] = player.PlayerCombatState?.Energy ?? 0,
                ["stars"] = player.PlayerCombatState?.Stars ?? 0,
                ["ready"] = PlayerScope.IsReadyToEndTurn(player),
                ["hp"] = player.Creature?.CurrentHp ?? 0,
                ["max_hp"] = player.Creature?.MaxHp ?? 0
            });
        }

        var data = new Dictionary<string, object>
        {
            ["combat_active"] = runtime.IsCombatActive,
            ["busy"] = runtime.IsBusy,
            ["singleplayer"] = runtime.IsSingleplayer,
            ["multiplayer"] = runtime.IsMultiplayer,
            ["multiplayer_like"] = runtime.IsMultiplayerLike,
            ["fake_multiplayer"] = runtime.IsFakeMultiplayer,
            ["net_type"] = PlayerScope.NetType.ToString(),
            ["local_net_id"] = PlayerScope.LocalNetId,
            ["players"] = players,
            ["quiescent"] = runtime.IsStable,
            ["last_quiescence_reason"] = runtime.LastQuiescenceReason,
            ["frontier"] = runtime.CurrentFrontier?.ToDictionary(),
            ["mp"] = RewindCoordinator.Instance.Status(),
            ["jump_pending"] = runtime.IsJumpPending,
            ["jump_request_id"] = runtime.LastJumpRequestId,
            ["prune_pending"] = RewindCoordinator.Instance.LastPrune?.Pending ?? false,
            ["combat_index"] = runtime.CombatIndex,
            ["node_count"] = runtime.Tree.Count,
            ["current_id"] = runtime.Tree.CurrentId,
            ["next_id"] = runtime.Tree.NextId,
            ["last_restore_mismatches"] = runtime.LastRestoreMismatches,
            ["last_restore_report"] = runtime.LastRestoreReport,
            ["last_deep_equal"] = runtime.LastRestoreDeepEqual,
            ["last_deep_hash"] = runtime.LastRestoreDeepHash,
            ["event_count"] = runtime.Events.Count,
            ["stable"] = runtime.IsStable,
            ["last_capture_skip_reason"] = runtime.LastCaptureSkipReason,
            ["port"] = _port
        };

        return JsonSerializer.Serialize(data);
    }

    private static string ToolTree()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        var nodes = new List<object>();

        foreach (RewindNode node in runtime.Tree.Nodes.Values.OrderBy(n => n.Id))
        {
            nodes.Add(new Dictionary<string, object>
            {
                ["id"] = node.Id,
                ["parent_id"] = node.ParentId,
                ["alias_of"] = node.AliasOf,
                ["portal_count"] = runtime.Tree.PortalCount(node.Id),
                ["label"] = node.Label,
                ["kind"] = node.Kind,
                ["round"] = node.Round,
                ["turn"] = node.Turn,
                ["checksum"] = node.Checksum.ToString("x8"),
                ["deep_hash"] = node.DeepHash,
                ["local_scope_hash"] = node.SharedDeepHash,
                ["shared"] = node.Shared,
                ["playable"] = node.Playable,
                ["state"] = node.State,
                ["participants"] = node.Participants,
                ["frontier"] = node.Frontier?.ToDictionary(),
                ["children"] = node.Children.ToArray(),
                ["has_snapshot"] = node.Snapshot != null,
                ["objects"] = node.ObjectCount,
                ["shared_objects"] = node.SharedObjectCount,
                ["shared_fields"] = node.SharedFieldCount
            });
        }

        var data = new Dictionary<string, object>
        {
            ["combat_active"] = runtime.IsCombatActive,
            ["current_id"] = runtime.Tree.CurrentId,
            ["node_count"] = runtime.Tree.Count,
            ["nodes"] = nodes
        };

        return JsonSerializer.Serialize(data);
    }

    private static string ToolCapture(string label)
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        if (!runtime.IsCombatActive)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "不在战斗中，没有可抓取的战斗状态"
            });
        }

        // 动作执行中抓点会得到一个"半应用"的状态：校验和不可复现，跳回去也没意义。
        if (runtime.IsBusy)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "有动作正在执行，等结算完再抓点"
            });
        }

        int beforeId = runtime.Tree.CurrentId;
        int beforeCount = runtime.Tree.Count;
        RewindNode node = runtime.CaptureNowForTest(label);

        if (node == null)
        {
            // 优先回显"稳定决策点"门禁的具体原因：结束回合过渡中抓点不是错误，
            // 而是设计上不该有节点，必须让调用方一眼看出是哪种情况。
            string skipped = runtime.LastCaptureSkipReason;
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = string.IsNullOrEmpty(skipped)
                    ? "抓点失败（无法计算校验和或快照异常），详见 game 日志"
                    : $"当前不是稳定决策点，未抓点：{skipped}",
                ["skipped_transition"] = !string.IsNullOrEmpty(skipped),
                ["quiescent"] = runtime.IsStable,
                ["quiescence_reason"] = runtime.LastQuiescenceReason
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["node_id"] = node.Id,
            ["merged"] = runtime.Tree.Count == beforeCount,
            ["previous_id"] = beforeId,
            ["node_count"] = runtime.Tree.Count,
            ["checksum"] = node.Checksum.ToString("x8"),
            ["shared"] = node.Shared,
            ["playable"] = node.Playable,
            ["state"] = node.State,
            ["local_scope_hash"] = node.SharedDeepHash,
            ["participants"] = node.Participants,
            ["frontier"] = node.Frontier?.ToDictionary(),
            ["label"] = node.Label
        });
    }

    private static string ToolJump(int id)
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        bool multiplayer = PlayerScope.IsMultiplayer;
        bool ok = runtime.JumpTo(id, out string error);
        RewindNode current = runtime.Tree.Current;

        var data = new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["pending"] = runtime.IsJumpPending,
            ["request_id"] = runtime.LastJumpRequestId,
            ["mode"] = multiplayer ? "mp" : "local",
            ["requested_id"] = id,
            ["current_id"] = runtime.Tree.CurrentId,
            ["node_count"] = runtime.Tree.Count,
            ["checksum"] = ok && !runtime.IsJumpPending && current != null
                ? current.Checksum.ToString("x8")
                : null,
            ["verify_mismatches"] = runtime.LastRestoreMismatches,
            ["verify_report"] = runtime.LastRestoreReport,
            ["deep_equal"] = runtime.LastRestoreDeepEqual,
            ["deep_hash"] = runtime.LastRestoreDeepHash,
            ["error"] = error
        };

        if (multiplayer)
        {
            data["mp"] = RewindCoordinator.Instance.Status();
        }

        return JsonSerializer.Serialize(data);
    }

    private static string ToolJumpStatus()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        RewindCoordinator coordinator = RewindCoordinator.Instance;

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = !(coordinator.LastResult?.Pending ?? false),
            ["multiplayer"] = PlayerScope.IsMultiplayer,
            ["pending"] = runtime.IsJumpPending,
            ["request_id"] = runtime.LastJumpRequestId,
            ["current_id"] = runtime.Tree.CurrentId,
            ["node_count"] = runtime.Tree.Count,
            ["last_result"] = coordinator.LastResult?.ToDictionary(),
            ["mp"] = coordinator.Status()
        });
    }

    private static string ToolMpStatus()
    {
        return JsonSerializer.Serialize(RewindCoordinator.Instance.Status());
    }

    private static string ToolPoisonRestore()
    {
        if (!FakeMultiplayerHarness.Enabled)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game"
            });
        }

        RewindCoordinator.Instance.PoisonNextRestore();
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["armed"] = true
        });
    }

    /// <summary>
    /// 测试脚手架：一次把前 N 张可打出的牌直接入队，模拟“玩家快速连点”。
    /// 游戏侧 REST API 在动作执行期间会 409，脚本没法制造「队列里先有下一张」的时序，
    /// 所以只能在进程内直接调 EnqueueWithoutSynchronizing。
    /// </summary>
    private static string ToolBurst(int count)
    {
        if (!FakeMultiplayerHarness.Enabled)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game"
            });
        }

        int limit = Math.Clamp(count <= 0 ? 2 : count, 1, 4);
        RunManager manager = RunManager.Instance;
        Player me = PlayerScope.Local;
        CombatState combat = PlayerScope.CombatState;

        if (manager?.ActionQueueSynchronizer == null || me?.PlayerCombatState == null || combat == null)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "不在战斗中"
            });
        }

        var picked = new List<string>(limit);
        foreach (CardModel card in me.PlayerCombatState.Hand.Cards.ToList())
        {
            if (picked.Count >= limit)
            {
                break;
            }

            if (!card.CanPlay())
            {
                continue;
            }

            Creature target = null;
            if (card.TargetType.IsSingleTarget())
            {
                target = combat.Creatures.FirstOrDefault(creature =>
                    creature.IsAlive && card.IsValidTarget(creature));
                if (target == null)
                {
                    continue;
                }
            }

            // 走正常入队路径：房主直接入队并广播；客户端会转成请求发给房主。
            manager.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
            picked.Add(target == null ? card.Id.Entry : $"{card.Id.Entry}->{target.Name}");
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = picked.Count > 0,
            ["enqueued"] = picked.Count,
            ["cards"] = picked,
            ["error"] = picked.Count == 0 ? "没有可打出的牌" : null
        });
    }

    private static string ToolState()
    {
        return JsonSerializer.Serialize(LiveStateProbe.Capture());
    }

    private static string ToolNode(int id)
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        RewindNode node = runtime.Tree.Get(id);
        if (node == null)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = $"节点 #{id} 不存在"
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["id"] = node.Id,
            ["parent_id"] = node.ParentId,
            ["alias_of"] = node.AliasOf,
            ["canonical_id"] = node.AliasOf != 0 ? node.AliasOf : node.Id,
            ["portal_count"] = runtime.Tree.PortalCount(node.Id),
            ["label"] = node.Label,
            ["kind"] = node.Kind,
            ["round"] = node.Round,
            ["turn"] = node.Turn,
            ["checksum"] = node.Checksum.ToString("x8"),
            ["deep_hash"] = node.DeepHash,
            ["local_scope_hash"] = node.SharedDeepHash,
            ["shared"] = node.Shared,
            ["playable"] = node.Playable,
            ["state"] = node.State,
            ["participants"] = node.Participants,
            ["frontier"] = node.Frontier?.ToDictionary(),
            ["objects"] = node.ObjectCount,
            ["fields"] = node.FieldCount,
            ["shared_objects"] = node.SharedObjectCount,
            ["shared_fields"] = node.SharedFieldCount,
            ["has_snapshot"] = node.Snapshot != null,
            ["digest"] = node.Digest
        });
    }

    private static string ToolVerify()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        bool clean = runtime.VerifyCurrent(out string summary, out int mismatches);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = clean,
            ["current_id"] = runtime.Tree.CurrentId,
            ["mismatches"] = mismatches,
            ["report"] = summary
        });
    }

    private static string ToolEvents(int last, string kind, ulong actor, string stream)
    {
        return RewindRuntime.Instance.Events.ToJson(last > 0 ? last : 0, kind, actor, stream);
    }

    private static ulong GetULong(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out ulong parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string ToolJumpParent()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        int before = runtime.Tree.CurrentId;
        bool ok = runtime.JumpToParent(out string error);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["from_id"] = before,
            ["current_id"] = runtime.Tree.CurrentId,
            ["error"] = error
        });
    }

    private static string ToolJumpForward()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        int before = runtime.Tree.CurrentId;
        bool ok = runtime.JumpToForward(out string error);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["from_id"] = before,
            ["current_id"] = runtime.Tree.CurrentId,
            ["error"] = error
        });
    }

    private static string ToolDiffNodes(int idA, int idB)
    {
        if (!FakeMultiplayerHarness.Enabled)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "disabled: set GLOBAL_REWIND_TEST_HARNESS=1 before launching the game"
            });
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        RewindNode a = runtime.Tree.Get(idA);
        RewindNode b = runtime.Tree.Get(idB);
        if (a?.Snapshot == null || b?.Snapshot == null)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "两个节点都必须有内存快照"
            });
        }

        Dictionary<string, int> censusA = a.Snapshot.DebugRecordCensus();
        Dictionary<string, int> censusB = b.Snapshot.DebugRecordCensus();
        var censusDiff = new List<object>();
        foreach (string type in censusA.Keys.Union(censusB.Keys).OrderBy(value => value))
        {
            censusA.TryGetValue(type, out int countA);
            censusB.TryGetValue(type, out int countB);
            if (countA != countB)
            {
                censusDiff.Add(new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["a"] = countA,
                    ["b"] = countB
                });
            }
        }

        string textA = a.Snapshot.DebugCanonical(HashScope.Full);
        string textB = b.Snapshot.DebugCanonical(HashScope.Full);
        string[] linesA = textA.Split('\n');
        string[] linesB = textB.Split('\n');
        var lineDiff = new List<string>(12);
        int lineCount = Math.Max(linesA.Length, linesB.Length);
        for (int i = 0; i < lineCount && lineDiff.Count < 12; i++)
        {
            string left = i < linesA.Length ? linesA[i] : "<missing>";
            string right = i < linesB.Length ? linesB[i] : "<missing>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                lineDiff.Add($"line {i + 1}\n  a: {left}\n  b: {right}");
            }
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["a"] = idA,
            ["b"] = idB,
            ["census_diff"] = censusDiff,
            ["lines_a"] = linesA.Length,
            ["lines_b"] = linesB.Length,
            ["line_diff"] = lineDiff,
            ["hash_a"] = a.Snapshot.DeepHash,
            ["hash_b"] = b.Snapshot.DeepHash,
            ["merge_hash_a"] = a.Snapshot.MergeHash,
            ["merge_hash_b"] = b.Snapshot.MergeHash,
            ["shared_merge_hash_a"] = a.Snapshot.SharedMergeHash,
            ["shared_merge_hash_b"] = b.Snapshot.SharedMergeHash
        });
    }

    private static string ToolPrune(int id)
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        bool multiplayer = PlayerScope.IsMultiplayer;
        int beforeCount = runtime.Tree.Count;
        bool ok = runtime.Prune(id, out string error);

        RewindCoordinator.PruneResult last = RewindCoordinator.Instance.LastPrune;
        bool pending = multiplayer && ok && (last?.Pending ?? false);

        var data = new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["pending"] = pending,
            ["request_id"] = multiplayer ? last?.RequestId ?? 0 : 0,
            ["mode"] = multiplayer ? "mp" : "local",
            ["pruned_id"] = id,
            ["node_count"] = runtime.Tree.Count,
            ["removed"] = beforeCount - runtime.Tree.Count,
            ["current_id"] = runtime.Tree.CurrentId,
            ["error"] = error
        };

        if (multiplayer)
        {
            data["mp"] = RewindCoordinator.Instance.Status();
        }

        return JsonSerializer.Serialize(data);
    }

    private static string ToolPruneStatus()
    {
        RewindCoordinator coordinator = RewindCoordinator.Instance;
        RewindCoordinator.PruneResult last = coordinator.LastPrune;

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = !(last?.Pending ?? false) && (last?.Ok ?? true),
            ["multiplayer"] = PlayerScope.IsMultiplayer,
            ["pending"] = last?.Pending ?? false,
            ["last_result"] = last?.ToDictionary(),
            ["mp"] = coordinator.Status()
        });
    }

    private static string ToolFrontier()
    {
        RewindRuntime runtime = RewindRuntime.Instance;
        bool quiescent = QuiescenceProbe.TryCapture(out Frontier frontier, out string reason);
        frontier ??= runtime.CurrentFrontier;

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = frontier != null,
            ["quiescent"] = quiescent,
            ["reason"] = reason,
            ["frontier"] = frontier?.ToDictionary(),
            ["multiplayer"] = runtime.IsMultiplayer,
            ["fake_multiplayer"] = runtime.IsFakeMultiplayer,
            ["local_net_id"] = PlayerScope.LocalNetId,
            ["combat_active"] = runtime.IsCombatActive,
            ["current_id"] = runtime.Tree.CurrentId
        });
    }

    private static string ToolChecksum()
    {
        if (!CombatChecksum.ComputePure(out uint checksum))
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "当前没有可计算的战斗状态"
            });
        }

        string digest = null;
        if (CombatChecksum.TryBuildNormalized(out var normalized, out _))
        {
            digest = CombatChecksum.BuildDigest(normalized);
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["checksum"] = checksum.ToString("x8"),
            ["digest"] = digest,
            ["current_id"] = RewindRuntime.Instance.Tree.CurrentId
        });
    }
}
