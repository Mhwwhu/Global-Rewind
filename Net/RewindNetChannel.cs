using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using GlobalRewind.Logging;

namespace GlobalRewind.Net;

/// <summary>
/// 把 <see cref="INetGameService"/> 收成一份很薄的发送/订阅门面。
///
/// 只做三件事：
///   · 按当前这局的 NetService 幂等注册/注销 M2 消息处理器；
///   · 房主广播 / 房主单发 / 客户端回房主；
///   · 读当前还在线的 peer 列表（房主视角）。
///
/// 真正的协议状态机在 <see cref="RewindCoordinator"/>，这里不存任何业务状态。
/// </summary>
internal sealed class RewindNetChannel
{
    private readonly List<Action> _unregister = new List<Action>(16);

    private INetGameService _service;
    private Action<INetMessage, ulong> _onMessage;

    public INetGameService Service => _service;

    public bool IsHost => _service != null && _service.Type == NetGameType.Host;

    public bool IsClient => _service != null && _service.Type == NetGameType.Client;

    public bool IsMultiplayer => IsHost || IsClient;

    public bool IsConnected
    {
        get
        {
            try
            {
                return _service != null && _service.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 幂等订阅：同一局里 NetService 不会换对象，换了就说明是新的一局，重新挂。
    /// 返回 true 表示这次真的（重新）注册了。
    /// </summary>
    public bool Ensure(INetGameService service, Action<INetMessage, ulong> onMessage)
    {
        if (service == null)
        {
            return false;
        }

        if (ReferenceEquals(_service, service) && _unregister.Count > 0)
        {
            return false;
        }

        Unregister();
        _service = service;
        _onMessage = onMessage;

        Register<GrHelloMessage>(service);
        Register<GrHelloAckMessage>(service);
        Register<GrAnchorMessage>(service);
        Register<GrAnchorAckMessage>(service);
        Register<GrFreezeMessage>(service);
        Register<GrIdleMessage>(service);
        Register<GrCommitMessage>(service);
        Register<GrRestoredMessage>(service);
        Register<GrResumeMessage>(service);
        Register<GrAbortMessage>(service);
        Register<GrJumpRequestMessage>(service);
        Register<GrCurrentMessage>(service);
        Register<GrPrunePrepareMessage>(service);
        Register<GrPruneReadyMessage>(service);
        Register<GrPruneCommitMessage>(service);
        Register<GrPruneDoneMessage>(service);
        Register<GrPruneAbortMessage>(service);

        ModLog.Info($"net subscribed ({service.Type})");
        return true;
    }

    public void Unregister()
    {
        foreach (Action action in _unregister)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ModLog.Warn($"net unsubscribe failed: {e.Message}");
            }
        }

        _unregister.Clear();
        _service = null;
        _onMessage = null;
    }

    /// <summary>房主广播给所有已完成握手的客户端。</summary>
    public bool SendToAll(GrMessage message)
    {
        if (!IsHost || !IsConnected || message == null)
        {
            return false;
        }

        try
        {
            _service.SendMessage(message);
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"net send {message.GetType().Name} to all failed: {e.Message}");
            return false;
        }
    }

    /// <summary>客户端发给房主。</summary>
    public bool SendToHost(GrMessage message)
    {
        if (!IsClient || !IsConnected || message == null)
        {
            return false;
        }

        try
        {
            _service.SendMessage(message);
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"net send {message.GetType().Name} to host failed: {e.Message}");
            return false;
        }
    }

    /// <summary>房主单发；对端不在线时返回 false。</summary>
    public bool SendToPeer(GrMessage message, ulong peerId)
    {
        if (!IsHost || !IsConnected || message == null)
        {
            return false;
        }

        try
        {
            _service.SendMessage(message, peerId);
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"net send {message.GetType().Name} to {peerId} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>房主视角：当前已连接且可以广播的 peer。</summary>
    public List<ulong> Peers()
    {
        var peers = new List<ulong>(4);

        if (!IsHost || _service is not INetHostGameService host)
        {
            return peers;
        }

        try
        {
            foreach (NetClientData client in host.ConnectedPeers)
            {
                peers.Add(client.peerId);
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"read peer list failed: {e.Message}");
        }

        return peers;
    }

    private void Register<T>(INetGameService service) where T : INetMessage
    {
        MessageHandlerDelegate<T> handler = (message, senderId) =>
        {
            try
            {
                _onMessage?.Invoke(message, senderId);
            }
            catch (Exception e)
            {
                ModLog.Warn($"net handler {typeof(T).Name} failed: {e.GetType().Name} {e.Message}");
            }
        };

        service.RegisterMessageHandler(handler);
        _unregister.Add(() => service.UnregisterMessageHandler(handler));
    }
}
