// ==============================================================================
// 文件: IpcBroker.cs
// 命名空间: BlueRandom.Core.Ipc
// 作用: 应用核心进程内通信总线（IPC Broker）。
//       基于 System.Threading.Channels 高性能无锁通道实现单读取、多写入（SingleReader, MultiWriter）
//       的消息发布-订阅系统，解耦各独立 STA 线程窗口与后台业务调度器。
// ==============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlueRandom.Core.Logging;

namespace BlueRandom.Core.Ipc;

/// <summary>
/// 核心通信总线 (@IPC Broker)。
/// 基于 .NET 内置高性能无锁队列 (System.Threading.Channels) 实现跨线程调度与消息路由分发。
/// </summary>
public static class IpcBroker
{
    private static readonly Channel<IpcMessage> _channel = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private static readonly ConcurrentDictionary<IpcModule, List<Action<IpcMessage>>> _subscribers = new();
    private static CancellationTokenSource? _cts;
    private static Task? _dispatchTask;
    private static bool _isInitialized = false;

    /// <summary>
    /// 初始化 IPC 通信总线并启动单线程后台异步分发消费循环。
    /// 若总线已初始化则直接幂等返回，使用 WaitToReadAsync 机制避免线程轮询自旋开销。
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        _cts = new CancellationTokenSource();

        _dispatchTask = Task.Run(async () =>
        {
            AppLogger.Info("IPC", "IPC 通信总线已启动分发循环");
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var msg))
                    {
                        DispatchMessage(msg);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                AppLogger.Error("IPC", "消息分发异常", ex);
            }
            AppLogger.Info("IPC", "IPC 通信总线已停止");
        }, _cts.Token);
    }

    /// <summary>
    /// 将 IPC 消息非阻塞地推入通信总线队列。
    /// 若总线尚未激活则自动触发延迟初始化；写入为极速内存操作，不会阻塞发送方线程。
    /// </summary>
    /// <param name="message">待投递的标准 IPC 消息对象。</param>
    public static void Send(IpcMessage message)
    {
        if (!_isInitialized) Initialize();
        _channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// 为指定的目标模块注册消息监听处理委托。
    /// 内部使用并发字典结合细粒度锁维护监听器列表，支持在多线程环境下动态增删。
    /// </summary>
    /// <param name="target">接收消息的目标模块枚举标识。</param>
    /// <param name="handler">消息到达时的同步或异步分发回调委托。</param>
    public static void Subscribe(IpcModule target, Action<IpcMessage> handler)
    {
        _subscribers.AddOrUpdate(target,
            _ => new List<Action<IpcMessage>> { handler },
            (_, list) =>
            {
                lock (list) { list.Add(handler); }
                return list;
            });
    }

    /// <summary>
    /// 注销指定目标模块上已挂载的特定消息监听委托。
    /// 若监听列表为空则保持字典项结构，避免反复哈希扩容分配。
    /// </summary>
    /// <param name="target">原注册的目标模块标识。</param>
    /// <param name="handler">先前挂载的回调委托引用。</param>
    public static void Unsubscribe(IpcModule target, Action<IpcMessage> handler)
    {
        if (_subscribers.TryGetValue(target, out var list))
        {
            lock (list) { list.Remove(handler); }
        }
    }

    /// <summary>
    /// 将从无锁管道中提取的消息同步广播给对应目标模块的所有已注册委托。
    /// 采用快照隔离执行（ToArray），并对每个回调独立进行 try-catch 异常隔离保护，防止单个监听器抛出异常中断整个分发循环。
    /// </summary>
    /// <param name="msg">自通道中出队的消息实例。</param>
    private static void DispatchMessage(IpcMessage msg)
    {
        if (_subscribers.TryGetValue(msg.Target, out var list))
        {
            Action<IpcMessage>[] handlers;
            lock (list)
            {
                handlers = list.ToArray();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(msg);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("IPC", $"执行消息处理器失败: {msg}", ex);
                }
            }
        }
        else
        {
            AppLogger.Warn("IPC", $"未找到目标模块监听器: {msg}");
        }
    }

    /// <summary>
    /// 优雅终止通信总线后台循环。
    /// 发出取消令牌信号，关闭管道写入端并释放调度资源。
    /// </summary>
    public static void Destroy()
    {
        _cts?.Cancel();
        _channel.Writer.TryComplete();
    }
}
