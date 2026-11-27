namespace GlobalRewind.Snapshot;

/// <summary>
/// 一次写回的结果。SP 的 JumpTo 用它决定是否把当前节点推进过去，
/// M2 的协同跳转用它决定广播 Commit 还是 Abort。
///
/// 失败时调用方**不需要**再回滚：<c>RewindRuntime.RestoreNodeSnapshot</c> 在返回前
/// 已经把安全快照写回去了；这里只回传“为什么失败”，供两端对齐原因。
/// </summary>
internal sealed class RestoreOutcome
{
    public bool Ok;
    public string Error;
    public int Mismatches;
    public bool DeepEqual;
    public string LiveHash;
    public uint Checksum;
    public string Report;
    public string DeepDetail;

    public override string ToString()
    {
        return Ok
            ? $"ok checksum={Checksum:x8} mismatches={Mismatches} deep_equal={DeepEqual}"
            : $"failed: {Error}";
    }
}
