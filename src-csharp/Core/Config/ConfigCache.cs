// ==============================================================================
// 文件: ConfigCache.cs
// 命名空间: BlueRandom.Core.Config
// 作用: 内存配置缓存单例，维护当前运行态 RootConfig 快照，提供线程安全读写、
//       原子性字段变异（Mutate）以及跨模块热更新事件通知（ConfigUpdated）。
// ==============================================================================

using System;
using System.Collections.Generic;

namespace BlueRandom.Core.Config;

/// <summary>
/// 内存配置缓存单例 (Config Cache)。
/// 驻留内存的预解析活跃配置，毫秒级查询，避免高频磁盘读取与序列化，提供线程安全的数据访问。
/// </summary>
public static class ConfigCache
{
    private static readonly object _lock = new();
    private static RootConfig _current = new();

    /// <summary>
    /// 当配置发生热更新时触发的多播委托事件。
    /// 订阅者包括窗体渲染器、调度引擎以及 IPC 广播器。
    /// </summary>
    public static event Action<RootConfig>? ConfigUpdated;

    /// <summary>
    /// 获取或设置当前生效配置快照。
    /// 读写操作通过排他锁保证原子性；赋值时若传入 null 则回退为默认配置，并在退出锁后触发热更新事件。
    /// </summary>
    public static RootConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
        set
        {
            lock (_lock)
            {
                _current = value ?? new RootConfig();
            }
            ConfigUpdated?.Invoke(_current);
        }
    }

    /// <summary>
    /// 线程安全地修改当前配置对象的指定字段。
    /// 在锁内部执行变异委托，确保字段更新时的并发一致性，并在释放锁后安全触发全局配置更新事件。
    /// </summary>
    /// <param name="updater">针对当前 RootConfig 实例执行修改的闭包委托。</param>
    public static void Mutate(Action<RootConfig> updater)
    {
        RootConfig snapshot;
        lock (_lock)
        {
            updater(_current);
            snapshot = _current;
        }
        ConfigUpdated?.Invoke(snapshot);
    }

    /// <summary>
    /// 获取当前抽取候选学生池的独立副本列表。
    /// 返回深浅拷贝以避免调用方在并发遍历过程中因外部修改触发集合并发修改异常。
    /// </summary>
    /// <returns>学生条目列表副本。</returns>
    public static List<StudentItem> GetStudentPool()
    {
        lock (_lock)
        {
            return new List<StudentItem>(_current.StudentList);
        }
    }

    /// <summary>
    /// 获取当前悬浮按钮（FCB）的外观与交互配置对象。
    /// </summary>
    /// <returns>悬浮按钮配置实例。</returns>
    public static FloatingButtonConfig GetFCBProps()
    {
        lock (_lock)
        {
            return _current.FloatingButton;
        }
    }

    /// <summary>
    /// 获取当前抽卡结果展示窗口（FRW）的动画、音效及透明度配置对象。
    /// </summary>
    /// <returns>结果浮窗配置实例。</returns>
    public static PickResultDialogConfig GetFRWProps()
    {
        lock (_lock)
        {
            return _current.PickResultDialog;
        }
    }
}
