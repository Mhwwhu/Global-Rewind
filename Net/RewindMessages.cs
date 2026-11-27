using System;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using GlobalRewind.Snapshot;

namespace GlobalRewind.Net;

/// <summary>
/// M2 协同跳转的自定义网络消息。
///
/// 这些类型会在游戏启动时被 <c>MessageTypes.Initialize()</c> 通过
/// <c>ReflectionHelper.GetSubtypesInMods&lt;INetMessage&gt;()</c> 自动登记，
/// 所以**所有参与者必须装同一版本的 GlobalRewind**：消息 id 是按类型名排序生成的，
/// 少一个类型会让后面所有消息 id 错位（游戏握手也会校验 gameplay mod 列表）。
///
/// 约定：
///   · 房主 → 客户端用 Reliable（冻结/提交/恢复不能丢）；
///   · 客户端 → 房主用 Reliable，<c>ShouldBroadcast=false</c>（只回给房主）；
///   · <c>ShouldBuffer=false</c>：回溯协议消息带请求 id 和时效，缓冲后重放只会是过期指令。
/// </summary>
internal static class RewindNetProtocol
{
    /// <summary>协议版本。消息字段有任何不兼容改动就 +1。</summary>
    public const int Version = 2;

    /// <summary>本端 mod 版本（用于握手日志与不匹配提示）。</summary>
    public static string ModVersion
    {
        get
        {
            try
            {
                foreach (MegaCrit.Sts2.Core.Modding.Mod mod in MegaCrit.Sts2.Core.Modding.ModManager.Mods)
                {
                    if (string.Equals(mod.manifest?.id, "GlobalRewind", StringComparison.Ordinal))
                    {
                        return mod.manifest?.version ?? "unknown";
                    }
                }
            }
            catch
            {
                // ModManager 还没就绪时退回 unknown
            }

            return "unknown";
        }
    }
}

/// <summary>抽象基类：把四个 INetMessage 属性收在一处，避免每个消息重复声明。</summary>
public abstract class GrMessage : INetMessage
{
    public virtual bool ShouldBroadcast => false;

    public virtual NetTransferMode Mode => NetTransferMode.Reliable;

    public virtual LogLevel LogLevel => LogLevel.Info;

    public virtual bool ShouldBuffer => false;

    public abstract void Serialize(PacketWriter writer);

    public abstract void Deserialize(PacketReader reader);

    // ---------------------------------------------------------------- 公共字段读写

    protected static void WriteUInts(PacketWriter writer, uint[] values)
    {
        values ??= Array.Empty<uint>();
        writer.WriteInt(values.Length);
        foreach (uint value in values)
        {
            writer.WriteUInt(value);
        }
    }

    protected static uint[] ReadUInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<uint>();
        }

        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadUInt();
        }

        return values;
    }

    protected static void WriteInts(PacketWriter writer, int[] values)
    {
        values ??= Array.Empty<int>();
        writer.WriteInt(values.Length);
        foreach (int value in values)
        {
            writer.WriteInt(value);
        }
    }

    protected static int[] ReadInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<int>();
        }

        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt();
        }

        return values;
    }

    protected static void WriteULongs(PacketWriter writer, ulong[] values)
    {
        values ??= Array.Empty<ulong>();
        writer.WriteInt(values.Length);
        foreach (ulong value in values)
        {
            writer.WriteULong(value);
        }
    }

    protected static ulong[] ReadULongs(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<ulong>();
        }

        var values = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadULong();
        }

        return values;
    }

    protected static string Safe(string value)
    {
        return value ?? string.Empty;
    }
}

/// <summary>房主 → 客户端：进战斗握手。客户端回 <see cref="GrHelloAckMessage"/>。</summary>
public sealed class GrHelloMessage : GrMessage
{
    public int Protocol;
    public string ModVersion = string.Empty;
    public int CombatIndex;
    public uint RootChecksum;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(Safe(ModVersion));
        writer.WriteInt(CombatIndex);
        writer.WriteUInt(RootChecksum);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        ModVersion = reader.ReadString();
        CombatIndex = reader.ReadInt();
        RootChecksum = reader.ReadUInt();
    }
}

/// <summary>客户端 → 房主：握手回执，带上本端的协议版本与当前纯哈希。</summary>
public sealed class GrHelloAckMessage : GrMessage
{
    public int Protocol;
    public string ModVersion = string.Empty;
    public uint Checksum;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(Safe(ModVersion));
        writer.WriteUInt(Checksum);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        ModVersion = reader.ReadString();
        Checksum = reader.ReadUInt();
    }
}

/// <summary>
/// 房主 → 客户端：节点锚点。节点 id 由房主分配，客户端只镜像，不能自增。
/// 客户端按 checksum + frontier 找到同一逻辑点后抓自己的 memento，再回 ack。
/// </summary>
public sealed class GrAnchorMessage : GrMessage
{
    public int Protocol;
    public int NodeId;
    public int ParentId;
    public string Label = string.Empty;
    public string Kind = string.Empty;
    public uint Checksum;
    public bool Playable;
    public bool Force;
    public int Round;
    public int Turn;
    public ulong[] Participants = Array.Empty<ulong>();
    public Frontier Frontier;

    /// <summary>>0：本节点是传送门，指向房主端已有的规范节点。</summary>
    public int AliasOf;

    /// <summary>>0：本节点接管该 id 的规范身份（换主），客户端绑定后要按同一顺序搬家。</summary>
    public int DemoteOldId;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteInt(NodeId);
        writer.WriteInt(ParentId);
        writer.WriteString(Safe(Label));
        writer.WriteString(Safe(Kind));
        writer.WriteUInt(Checksum);
        writer.WriteBool(Playable);
        writer.WriteBool(Force);
        writer.WriteInt(Round);
        writer.WriteInt(Turn);
        WriteULongs(writer, Participants);
        (Frontier ?? new Frontier()).Write(writer);
        writer.WriteInt(AliasOf);
        writer.WriteInt(DemoteOldId);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        NodeId = reader.ReadInt();
        ParentId = reader.ReadInt();
        Label = reader.ReadString();
        Kind = reader.ReadString();
        Checksum = reader.ReadUInt();
        Playable = reader.ReadBool();
        Force = reader.ReadBool();
        Round = reader.ReadInt();
        Turn = reader.ReadInt();
        Participants = ReadULongs(reader);
        Frontier = Snapshot.Frontier.Read(reader);
        AliasOf = reader.ReadInt();
        DemoteOldId = reader.ReadInt();
    }
}

/// <summary>客户端 → 房主：锚点镜像结果。ok=false 时房主把节点标成 partial（不可跳）。</summary>
public sealed class GrAnchorAckMessage : GrMessage
{
    public int NodeId;
    public bool Ok;
    public uint Checksum;
    public string Reason = string.Empty;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(NodeId);
        writer.WriteBool(Ok);
        writer.WriteUInt(Checksum);
        writer.WriteString(Safe(Reason));
    }

    public override void Deserialize(PacketReader reader)
    {
        NodeId = reader.ReadInt();
        Ok = reader.ReadBool();
        Checksum = reader.ReadUInt();
        Reason = reader.ReadString();
    }
}

/// <summary>房主 → 全员：冻结。收到后禁输入、等本机静止，然后回 <see cref="GrIdleMessage"/>。</summary>
public sealed class GrFreezeMessage : GrMessage
{
    public int Protocol;
    public int RequestId;
    public int NodeId;
    public uint ExpectedChecksum;
    public int TimeoutMs;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteInt(RequestId);
        writer.WriteInt(NodeId);
        writer.WriteUInt(ExpectedChecksum);
        writer.WriteInt(TimeoutMs);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        RequestId = reader.ReadInt();
        NodeId = reader.ReadInt();
        ExpectedChecksum = reader.ReadUInt();
        TimeoutMs = reader.ReadInt();
    }
}

/// <summary>客户端 → 房主：本端已静止（或失败原因）。房主用它做跳转前的一致性核对。</summary>
public sealed class GrIdleMessage : GrMessage
{
    public int RequestId;
    public bool Ok;
    public uint Checksum;
    public string Reason = string.Empty;
    public Frontier Frontier;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteBool(Ok);
        writer.WriteUInt(Checksum);
        writer.WriteString(Safe(Reason));
        (Frontier ?? new Frontier()).Write(writer);
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        Ok = reader.ReadBool();
        Checksum = reader.ReadUInt();
        Reason = reader.ReadString();
        Frontier = Snapshot.Frontier.Read(reader);
    }
}

/// <summary>房主 → 全员：房主已恢复成功，各端各自写回并核对。</summary>
public sealed class GrCommitMessage : GrMessage
{
    public int RequestId;
    public int NodeId;
    public uint HostChecksum;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteInt(NodeId);
        writer.WriteUInt(HostChecksum);
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        NodeId = reader.ReadInt();
        HostChecksum = reader.ReadUInt();
    }
}

/// <summary>客户端 → 房主：本端恢复结果（含恢复后的纯哈希）。</summary>
public sealed class GrRestoredMessage : GrMessage
{
    public int RequestId;
    public bool Ok;
    public uint Checksum;
    public int Mismatches;
    public string Error = string.Empty;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteBool(Ok);
        writer.WriteUInt(Checksum);
        writer.WriteInt(Mismatches);
        writer.WriteString(Safe(Error));
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        Ok = reader.ReadBool();
        Checksum = reader.ReadUInt();
        Mismatches = reader.ReadInt();
        Error = reader.ReadString();
    }
}

/// <summary>房主 → 全员：全员恢复一致，重建画面并解冻。</summary>
public sealed class GrResumeMessage : GrMessage
{
    public int RequestId;
    public int NodeId;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteInt(NodeId);
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        NodeId = reader.ReadInt();
    }
}

/// <summary>房主 → 全员：中止。rollback=true 表示已经写回过的端要退回冻结时的安全快照。</summary>
public sealed class GrAbortMessage : GrMessage
{
    public int RequestId;
    public string Reason = string.Empty;
    public bool Rollback;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteString(Safe(Reason));
        writer.WriteBool(Rollback);
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        Reason = reader.ReadString();
        Rollback = reader.ReadBool();
    }
}

/// <summary>客户端 → 房主：请求房主发起一次协同跳转（v1 由房主决定是否执行）。</summary>
public sealed class GrJumpRequestMessage : GrMessage
{
    public int RequestId;
    public int NodeId;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(RequestId);
        writer.WriteInt(NodeId);
    }

    public override void Deserialize(PacketReader reader)
    {
        RequestId = reader.ReadInt();
        NodeId = reader.ReadInt();
    }
}

/// <summary>
/// 房主 → 客户端：当前节点指针。
/// 房主重放同一动作时会“合并到已有节点”（不新建、也就不发锚点），
/// 客户端必须跟着把 current 挪过去，否则两端的“我在哪个节点”会漂移。
/// </summary>
public sealed class GrCurrentMessage : GrMessage
{
    public int Protocol;
    public int NodeId;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteInt(NodeId);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        NodeId = reader.ReadInt();
    }
}

/// <summary>
/// 房主 → 全员：剪枝准备。各端先在本地校验（目标存在、不是根、当前节点不在子树里、
/// 处于全体静止点），再回 <see cref="GrPruneReadyMessage"/>。
/// 只有所有端都 ready 且树指纹一致，房主才会广播 Commit。
/// </summary>
public sealed class GrPrunePrepareMessage : GrMessage
{
    public int Protocol;
    public int PruneId;
    public int TargetId;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteInt(PruneId);
        writer.WriteInt(TargetId);
    }

    public override void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        PruneId = reader.ReadInt();
        TargetId = reader.ReadInt();
    }
}

/// <summary>客户端 → 房主：本端能否剪枝，以及剪枝前的树指纹（用于确认两端树完全一致）。</summary>
public sealed class GrPruneReadyMessage : GrMessage
{
    public int PruneId;
    public bool Ok;
    public string Reason = string.Empty;
    public int CurrentId;
    public int NodeCount;
    public string TreeHash = string.Empty;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(PruneId);
        writer.WriteBool(Ok);
        writer.WriteString(Safe(Reason));
        writer.WriteInt(CurrentId);
        writer.WriteInt(NodeCount);
        writer.WriteString(Safe(TreeHash));
    }

    public override void Deserialize(PacketReader reader)
    {
        PruneId = reader.ReadInt();
        Ok = reader.ReadBool();
        Reason = reader.ReadString();
        CurrentId = reader.ReadInt();
        NodeCount = reader.ReadInt();
        TreeHash = reader.ReadString();
    }
}

/// <summary>房主 → 全员：所有端已确认，执行同一次确定性剪枝。</summary>
public sealed class GrPruneCommitMessage : GrMessage
{
    public int PruneId;
    public int TargetId;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(PruneId);
        writer.WriteInt(TargetId);
    }

    public override void Deserialize(PacketReader reader)
    {
        PruneId = reader.ReadInt();
        TargetId = reader.ReadInt();
    }
}

/// <summary>客户端 → 房主：剪枝已执行（附剪枝后的树指纹）。</summary>
public sealed class GrPruneDoneMessage : GrMessage
{
    public int PruneId;
    public bool Ok;
    public string Reason = string.Empty;
    public int NodeCount;
    public string TreeHash = string.Empty;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(PruneId);
        writer.WriteBool(Ok);
        writer.WriteString(Safe(Reason));
        writer.WriteInt(NodeCount);
        writer.WriteString(Safe(TreeHash));
    }

    public override void Deserialize(PacketReader reader)
    {
        PruneId = reader.ReadInt();
        Ok = reader.ReadBool();
        Reason = reader.ReadString();
        NodeCount = reader.ReadInt();
        TreeHash = reader.ReadString();
    }
}

/// <summary>房主 → 全员：剪枝中止（任一端未就绪 / 树不一致 / 超时）。所有人都不动树。</summary>
public sealed class GrPruneAbortMessage : GrMessage
{
    public int PruneId;
    public string Reason = string.Empty;

    public override bool ShouldBroadcast => true;

    public override void Serialize(PacketWriter writer)
    {
        writer.WriteInt(PruneId);
        writer.WriteString(Safe(Reason));
    }

    public override void Deserialize(PacketReader reader)
    {
        PruneId = reader.ReadInt();
        Reason = reader.ReadString();
    }
}
