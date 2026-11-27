using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using GlobalRewind.Logging;
using GlobalRewind.Snapshot;

namespace GlobalRewind.Tree;

/// <summary>
/// 探索树的落盘层。
///
/// 目录结构（%APPDATA%\SlayTheSpire2\global_rewind）：
///   run.json                     本局标识（seed / 开始时间）
///   combat_1\tree.json           第 1 场战斗的树结构（节点、父子、编号、校验和）
///   combat_1\nodes\3.json        3 号节点的摘要（对象数、生命/能量/牌堆规模，便于离线查看与对账）
///
/// 整局结束（RunManager.CleanUp / 通关 / 死亡）时整个目录会被删除。
/// </summary>
internal sealed class RewindStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string _runDir;
    private string _combatDir;

    public string RunDirectory => _runDir;

    public string CombatDirectory => _combatDir;

    public static string RootDirectory
    {
        get
        {
            // 先看环境变量：双实例测试用 `APPDATA=<独立目录>` 隔离落盘，
            // 但 Windows 上 `GetFolderPath(ApplicationData)` 走的是已知文件夹（注册表），
            // **不会**跟着进程的 APPDATA 变 —— 只认它会让两个实例写同一份 tree.json。
            string appData = System.Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            }

            if (string.IsNullOrEmpty(appData))
            {
                appData = OS.GetUserDataDir();
            }

            return Path.Combine(appData, "SlayTheSpire2", "global_rewind");
        }
    }

    public void PrepareRun(string seed)
    {
        try
        {
            DeleteAll();
            _runDir = RootDirectory;
            Directory.CreateDirectory(_runDir);

            WriteAtomic(Path.Combine(_runDir, "run.json"), JsonSerializer.Serialize(new RunDto
            {
                Seed = seed ?? string.Empty,
                StartedUtc = DateTime.UtcNow.ToString("O")
            }, JsonOptions));

            ModLog.Info($"store ready: {_runDir}");
        }
        catch (Exception e)
        {
            ModLog.Warn($"prepare run dir failed: {e.Message}");
            _runDir = null;
        }
    }

    public void BeginCombat(int combatIndex, string encounter)
    {
        if (_runDir == null)
        {
            return;
        }

        try
        {
            _combatDir = Path.Combine(_runDir, $"combat_{combatIndex}");
            Directory.CreateDirectory(_combatDir);
            Directory.CreateDirectory(Path.Combine(_combatDir, "nodes"));

            WriteAtomic(Path.Combine(_combatDir, "meta.json"), JsonSerializer.Serialize(new CombatDto
            {
                Index = combatIndex,
                Encounter = encounter ?? string.Empty,
                StartedUtc = DateTime.UtcNow.ToString("O")
            }, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Warn($"begin combat dir failed: {e.Message}");
            _combatDir = null;
        }
    }

    public void SaveTree(RewindTree tree)
    {
        if (_combatDir == null || tree == null)
        {
            return;
        }

        try
        {
            var dto = new TreeDto
            {
                NextId = tree.NextId,
                CurrentId = tree.CurrentId,
                Nodes = new List<NodeDto>(tree.Count)
            };

            var ids = new List<int>(tree.Nodes.Keys);
            ids.Sort();

            foreach (int id in ids)
            {
                RewindNode node = tree.Get(id);
                if (node == null)
                {
                    continue;
                }

                dto.Nodes.Add(new NodeDto
                {
                    Id = node.Id,
                    Parent = node.ParentId,
                    AliasOf = node.AliasOf,
                    Label = node.Label,
                    Kind = node.Kind,
                    Round = node.Round,
                    Turn = node.Turn,
                    Checksum = node.Checksum,
                    DeepHash = node.DeepHash,
                    SharedDeepHash = node.SharedDeepHash,
                    Objects = node.ObjectCount,
                    Fields = node.FieldCount,
                    SharedObjects = node.SharedObjectCount,
                    SharedFields = node.SharedFieldCount,
                    Shared = node.Shared,
                    Playable = node.Playable,
                    State = node.State,
                    Participants = node.Participants,
                    Frontier = node.Frontier,
                    CreatedUtc = new DateTime(node.CreatedUtcTicks, DateTimeKind.Utc).ToString("O"),
                    Children = node.Children.ToArray()
                });
            }

            WriteAtomic(Path.Combine(_combatDir, "tree.json"), JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Warn($"save tree failed: {e.Message}");
        }
    }

    /// <summary>
    /// 单个节点的摘要。只在建点时写一次，避免每加一个节点就重写全部摘要。
    /// </summary>
    public void SaveNode(RewindNode node)
    {
        if (_combatDir == null || node == null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(_combatDir, "nodes", $"{node.Id}.json");
            WriteAtomic(path, JsonSerializer.Serialize(new NodeDigestDto
            {
                Id = node.Id,
                Parent = node.ParentId,
                AliasOf = node.AliasOf,
                Label = node.Label,
                Kind = node.Kind,
                Round = node.Round,
                Turn = node.Turn,
                Checksum = node.Checksum,
                DeepHash = node.DeepHash,
                SharedDeepHash = node.SharedDeepHash,
                Objects = node.ObjectCount,
                Fields = node.FieldCount,
                SharedObjects = node.SharedObjectCount,
                SharedFields = node.SharedFieldCount,
                Shared = node.Shared,
                Playable = node.Playable,
                State = node.State,
                Participants = node.Participants,
                Frontier = node.Frontier
            }, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Warn($"save node #{node.Id} failed: {e.Message}");
        }
    }

    /// <summary>
    /// 删掉单个节点的落盘摘要。剪枝后必须调用：tree.json 已经不列这个节点，
    /// 留着 nodes/N.json 只会变成永远访问不到的孤儿文件。
    /// </summary>
    public void DeleteNode(int id)
    {
        if (_combatDir == null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(_combatDir, "nodes", $"{id}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"delete node #{id} failed: {e.Message}");
        }
    }

    public void DeleteAll()
    {
        try
        {
            string root = RootDirectory;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"cleanup store failed: {e.Message}");
        }

        _runDir = null;
        _combatDir = null;
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temp, path);
    }

    private sealed class RunDto
    {
        [JsonPropertyName("seed")] public string Seed { get; set; }
        [JsonPropertyName("started_utc")] public string StartedUtc { get; set; }
    }

    private sealed class CombatDto
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("encounter")] public string Encounter { get; set; }
        [JsonPropertyName("started_utc")] public string StartedUtc { get; set; }
    }

    private sealed class TreeDto
    {
        [JsonPropertyName("next_id")] public int NextId { get; set; }
        [JsonPropertyName("current_id")] public int CurrentId { get; set; }
        [JsonPropertyName("nodes")] public List<NodeDto> Nodes { get; set; }
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("parent")] public int Parent { get; set; }

        /// <summary>>0：传送门，指向规范节点；0：普通节点。</summary>
        [JsonPropertyName("alias_of")] public int AliasOf { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("round")] public int Round { get; set; }
        [JsonPropertyName("turn")] public int Turn { get; set; }
        [JsonPropertyName("checksum")] public uint Checksum { get; set; }
        [JsonPropertyName("deep_hash")] public string DeepHash { get; set; }
        [JsonPropertyName("local_scope_hash")] public string SharedDeepHash { get; set; }
        [JsonPropertyName("objects")] public int Objects { get; set; }
        [JsonPropertyName("fields")] public int Fields { get; set; }
        [JsonPropertyName("shared_objects")] public int SharedObjects { get; set; }
        [JsonPropertyName("shared_fields")] public int SharedFields { get; set; }
        [JsonPropertyName("shared")] public bool Shared { get; set; }
        [JsonPropertyName("playable")] public bool Playable { get; set; }
        [JsonPropertyName("state")] public string State { get; set; }
        [JsonPropertyName("participants")] public ulong[] Participants { get; set; }
        [JsonPropertyName("frontier")] public Frontier Frontier { get; set; }
        [JsonPropertyName("created_utc")] public string CreatedUtc { get; set; }
        [JsonPropertyName("children")] public int[] Children { get; set; }
    }

    private sealed class NodeDigestDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("parent")] public int Parent { get; set; }

        /// <summary>>0：传送门，指向规范节点；0：普通节点。</summary>
        [JsonPropertyName("alias_of")] public int AliasOf { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("round")] public int Round { get; set; }
        [JsonPropertyName("turn")] public int Turn { get; set; }
        [JsonPropertyName("checksum")] public uint Checksum { get; set; }
        [JsonPropertyName("deep_hash")] public string DeepHash { get; set; }
        [JsonPropertyName("local_scope_hash")] public string SharedDeepHash { get; set; }
        [JsonPropertyName("objects")] public int Objects { get; set; }
        [JsonPropertyName("fields")] public int Fields { get; set; }
        [JsonPropertyName("shared_objects")] public int SharedObjects { get; set; }
        [JsonPropertyName("shared_fields")] public int SharedFields { get; set; }
        [JsonPropertyName("shared")] public bool Shared { get; set; }
        [JsonPropertyName("playable")] public bool Playable { get; set; }
        [JsonPropertyName("state")] public string State { get; set; }
        [JsonPropertyName("participants")] public ulong[] Participants { get; set; }
        [JsonPropertyName("frontier")] public Frontier Frontier { get; set; }
    }
}
