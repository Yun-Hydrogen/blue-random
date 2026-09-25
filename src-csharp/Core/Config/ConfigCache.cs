using System;
using System.Collections.Generic;

namespace BlueRandom.Core.Config;

/// <summary>
/// 内存配置缓存单例 (Config Cache)
/// 驻留内存的预解析活跃配置，毫秒级查询，避免高频磁盘读取与序列化
/// </summary>
public static class ConfigCache
{
    private static readonly object _lock = new();
    private static RootConfig _current = new();

    /// <summary>
    /// 当配置发生热更新时触发
    /// </summary>
    public static event Action<RootConfig>? ConfigUpdated;

    /// <summary>
    /// 获取当前生效配置快照
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
    /// 安全修改当前配置字段
    /// </summary>
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
    /// 获取当前抽取候选学生池
    /// </summary>
    public static List<StudentItem> GetStudentPool()
    {
        lock (_lock)
        {
            return new List<StudentItem>(_current.StudentList);
        }
    }

    /// <summary>
    /// 获取悬浮按钮配置
    /// </summary>
    public static FloatingButtonConfig GetFCBProps()
    {
        lock (_lock)
        {
            return _current.FloatingButton;
        }
    }

    /// <summary>
    /// 获取结果浮窗配置
    /// </summary>
    public static PickResultDialogConfig GetFRWProps()
    {
        lock (_lock)
        {
            return _current.PickResultDialog;
        }
    }
}
