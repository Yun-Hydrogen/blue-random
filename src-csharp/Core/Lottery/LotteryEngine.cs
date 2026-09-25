using System;
using System.Collections.Generic;
using System.Linq;
using BlueRandom.Core.Config;

namespace BlueRandom.Core.Lottery;

/// <summary>
/// 纯领域抽卡算法引擎 (Lottery Engine)
/// 负责加权轮盘赌、无放回加权采样与信封等级映射
/// </summary>
public static class LotteryEngine
{
    private const double WeightBoostGamma = 1.5;
    private static readonly Random _random = new();

    /// <summary>
    /// 执行随机抽取
    /// </summary>
    /// <param name="pool">学生池</param>
    /// <param name="targetCount">抽取人数 (如 1 或 10)</param>
    /// <param name="allowDuplicate">是否允许单轮重复</param>
    /// <returns>抽取的学生列表</returns>
    public static List<CalledStudent> Pick(IReadOnlyList<StudentItem>? pool, int targetCount, bool allowDuplicate)
    {
        var result = new List<CalledStudent>();
        if (pool == null || pool.Count == 0 || targetCount <= 0)
        {
            return result;
        }

        // 规范化名单条目
        var validStudents = pool.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (validStudents.Count == 0) return result;

        if (allowDuplicate)
        {
            return PickWithDuplicate(validStudents, targetCount);
        }
        else
        {
            return PickWithoutDuplicate(validStudents, targetCount);
        }
    }

    /// <summary>
    /// 允许重复抽取（带权轮盘赌 + Gamma 增强）
    /// </summary>
    private static List<CalledStudent> PickWithDuplicate(List<StudentItem> students, int count)
    {
        var result = new List<CalledStudent>(count);

        // 计算增强权重
        var weightedPool = students.Select(s => new
        {
            Student = s,
            BoostedWeight = Math.Pow(Math.Max(0.0, s.Weight), WeightBoostGamma)
        }).ToList();

        double totalWeight = weightedPool.Sum(x => x.BoostedWeight);

        for (int i = 0; i < count; i++)
        {
            if (totalWeight <= 0.0)
            {
                // 全 0 权重：均匀随机抽取
                int idx = _random.Next(weightedPool.Count);
                result.Add(MapToCalled(weightedPool[idx].Student));
            }
            else
            {
                double roll = _random.NextDouble() * totalWeight;
                int selectedIndex = 0;
                for (int j = 0; j < weightedPool.Count; j++)
                {
                    roll -= weightedPool[j].BoostedWeight;
                    if (roll <= 0.0)
                    {
                        selectedIndex = j;
                        break;
                    }
                }
                result.Add(MapToCalled(weightedPool[selectedIndex].Student));
            }
        }

        return result;
    }

    /// <summary>
    /// 禁止重复抽取（Efraimidis-Spirakis 加权无放回采样 + 0 权重 Fisher-Yates 兜底）
    /// </summary>
    private static List<CalledStudent> PickWithoutDuplicate(List<StudentItem> students, int count)
    {
        var result = new List<CalledStudent>();
        var positivePool = students.Where(s => s.Weight > 0).ToList();
        var zeroPool = students.Where(s => s.Weight <= 0).ToList();

        // 1. 正权重池：通过 key = -ln(U) / weight 升序排序
        if (positivePool.Count > 0)
        {
            var keyed = positivePool.Select(s => new
            {
                Student = s,
                Key = -Math.Log(Math.Max(1e-10, _random.NextDouble())) / s.Weight
            }).OrderBy(x => x.Key).ToList();

            int takeCount = Math.Min(count, keyed.Count);
            for (int i = 0; i < takeCount; i++)
            {
                result.Add(MapToCalled(keyed[i].Student));
            }
        }

        // 2. 0 权重池：正权重人数不足时由零权重补充
        if (result.Count < count && zeroPool.Count > 0)
        {
            var remaining = new List<StudentItem>(zeroPool);
            // Fisher-Yates 洗牌
            for (int i = remaining.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
            }

            int fillCount = Math.Min(count - result.Count, remaining.Count);
            for (int i = 0; i < fillCount; i++)
            {
                result.Add(MapToCalled(remaining[i]));
            }
        }

        return result;
    }

    private static CalledStudent MapToCalled(StudentItem s)
    {
        string color = s.LetterColor?.ToLowerInvariant() switch
        {
            "gold" => "gold",
            "blue" => "blue",
            _ => "rainbow"
        };

        return new CalledStudent
        {
            Name = s.Name.Trim(),
            LetterColor = color
        };
    }
}
