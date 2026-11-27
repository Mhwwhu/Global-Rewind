using Godot;
using GlobalRewind.Logging;
using GlobalRewind.Net;
using MegaCrit.Sts2.Core.Nodes;

namespace GlobalRewind.App;

/// <summary>
/// 不依赖 Harmony 的输入入口。
///
/// 实测 `NGame._Input` 的补丁有时收不到按键，所以这里准备了两层：
///   1. 常驻 Node 的 `_Input` / `_UnhandledInput`；
///   2. `SceneTree.ProcessFrame` 里按当前绑定键轮询 `Input.IsKeyPressed`（完全不依赖任何回调注册）。
/// 三条通路都走 <see cref="RewindHotkeys"/>，内部 150ms 去重。
/// </summary>
internal sealed partial class RewindInputNode : Node
{
    private bool _logged;

    public override void _Ready()
    {
        SetProcessInput(true);
        SetProcessUnhandledInput(true);
        ModLog.Info("input node ready");
    }

    public override void _Process(double delta)
    {
        if (_logged)
        {
            return;
        }

        _logged = true;
        ModLog.Info("input node processing");
    }

    public override void _Input(InputEvent @event)
    {
        RewindHotkeys.HandleEvent(@event, "node");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        RewindHotkeys.HandleEvent(@event, "unhandled");
    }
}

/// <summary>
/// 等 NGame 出现后挂输入节点，并持续按当前绑定键做轮询兜底。
/// </summary>
internal static class RewindInputInstaller
{
    private static bool _attached;

    public static void Install()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            ModLog.Warn("input installer: no SceneTree");
            return;
        }

        tree.ProcessFrame += OnProcessFrame;
        ModLog.Info("input installer waiting for NGame");
    }

    private static void OnProcessFrame()
    {
        if (!_attached)
        {
            TryAttachNode();
        }

        // 轮询兜底：即使 Harmony 与 Node 回调都不生效，这里也一定能看到按键。
        RewindHotkeys.Poll();

        // 多人局的动作结算后要等一帧才真正从队列/执行器摘干净；
        // 这里驱动延迟抓点，不额外引入节点或 Process 回调。
        RewindRuntime.Instance.PumpDeferredCapture();

        // M2 协同跳转：网络回调只入队，状态机统一在主线程推进。
        RewindCoordinator.Instance.Pump();
    }

    private static void TryAttachNode()
    {
        NGame game = NGame.Instance;
        if (game == null)
        {
            return;
        }

        if (game.GetNodeOrNull<RewindInputNode>("GlobalRewindInput") == null)
        {
            var node = new RewindInputNode
            {
                Name = "GlobalRewindInput",
                ProcessMode = Node.ProcessModeEnum.Always
            };
            node.SetProcessInput(true);
            node.SetProcessUnhandledInput(true);
            game.AddChild(node);
            ModLog.Info($"input node added (insideTree={node.IsInsideTree()})");
        }

        _attached = true;
    }
}
