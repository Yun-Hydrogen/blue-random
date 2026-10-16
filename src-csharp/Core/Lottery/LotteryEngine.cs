// ==============================================================================
// 文件: LotteryEngine.cs
// 命名空间: BlueRandom.Core.Lottery
// 作用: 纯领域抽卡随机算法引擎（Lottery Engine）。
//       实现非线性 Gamma 幂律加权轮盘赌（有放回抽取）、Efraimidis-Spirakis A-Res 
//       无放回加权水塘采样、Fisher-Yates 均匀洗牌兜底以及信封品阶颜色映射。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using BlueRandom.Core.Config;

namespace BlueRandom.Core.Lottery;

/// <summary>
/// 纯领域抽卡算法引擎 (Lottery Engine)。
/// 负责加权轮盘赌、无放回加权采样与信封等级映射，与具体 UI 渲染和存储解耦。
/// </summary>
public static class LotteryEngine
{
    /// <summary>
    /// 权重平滑增强指数（Gamma 曲线）。
    /// 当权重大于 1.0 时，通过 w' = w^1.5 适当拉开高中低权重的分布概率差距，提高抽卡动态感与期望感知。
    /// </summary>
    private const double WeightBoostGamma = 1.5;

    private static readonly Random _random = new();

    /// <summary>
    /// 执行统一随机抽取调度。
    /// 内部执行名单清洗（过滤空名单与纯空白名字条目），并根据 allowDuplicate 标志分流至对应的抽取策略。
    /// </summary>
    /// <param name="pool">候选学生池原始列表（可为空）。</param>
    /// <param name="targetCount">期望抽取的名单人数（如单抽 1，十连抽 10）。</param>
    /// <param name="allowDuplicate">是否允许在单轮抽取中重复出现同一名学生。</param>
    /// <returns>已抽取并完成信封品阶映射的学生结果列表；若池为空或目标人数非法则返回空列表。</returns>
    public static List<CalledStudent> Pick(IReadOnlyList<StudentItem>? pool, int targetCount, bool allowDuplicate)
    {
        var result = new List<CalledStudent>();
        if (pool == null || pool.Count == 0 || targetCount <= 0)
        {
            return result;
        }

        // 规范化名单条目：清洗空白名称
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
    /// 允许重复抽取策略：带权轮盘赌（Roulette-Wheel Selection）结合非线性 Gamma 增益。
    /// <para>
    /// 算法流程：
    /// 1. 对每个学生的基础权重应用幂律变换: $w_i' = \max(0, w_i)^\gamma$（$\gamma = 1.5$）；
    /// 2. 统计累积总权重 $W = \sum w_i'$；
    /// 3. 若 $W \le 0$（所有候选人权重均为 0），退化为完全平权（$1/N$）的离散均匀随机采样；
    /// 4. 若 $W > 0$，每次抽取在 $[0, W)$ 区间内生成均匀伪随机实数 $R$，沿轮盘区间线性累减命中目标候选人；
    /// 5. 循环执行 targetCount 次，允许单次抽取内同一学生被多次命中。
    /// </para>
    /// </summary>
    /// <param name="students">已清洗的非空候选学生列表。</param>
    /// <param name="count">目标抽取总人数。</param>
    /// <returns>包含品阶映射的抽出学生列表。</returns>
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
    /// 禁止重复抽取策略：Efraimidis-Spirakis (A-Res) 加权无放回采样结合 Fisher-Yates 兜底。
    /// <para>
    /// 数学原理：
    /// 1. 采用 Pavlos S. Efraimidis 与 Paul G. Spirakis (2006) 提出的加权随机采样算法（Weighted Random Sampling without Replacement, A-Res）。
    /// 2. 对于正权重集合中的每个候选人 $i$（权重 $w_i > 0$），在 $(0, 1)$ 区间内独立抽取均匀实数 $U_i \sim U(0, 1)$。
    /// 3. 计算采样键值 $k_i = -\frac{\ln(U_i)}{w_i}$。按 $k_i$ 升序排序取前 $K$ 项，数学上严格证明其抽样概率分布等价于按权重连续无放回抽样。
    ///    时间复杂度为最优的 $O(N \log N)$（全量排序）。
    /// 4. 边界处理：若正权重集合的人数不足 targetCount，则剩余名额进入 0 权重候选池；
    ///    对 0 权重池采用经典无偏 Knuth / Fisher-Yates 乱序洗牌算法，按 $O(M)$ 线性步进填充，确保所有 0 权重候选人享有均等被补选机会。
    /// </para>
    /// </summary>
    /// <param name="students">已清洗的候选学生列表。</param>
    /// <param name="count">目标抽取总人数。</param>
    /// <returns>去重后的抽取学生列表（最大长度不超过输入学生总数）。</returns>
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

    /// <summary>
    /// 将配置模型中的 StudentItem 转换为面向展示层的 CalledStudent 领域实体。
    /// 校验信封颜色合法性（仅允许 rainbow、gold、blue），并回退不合法值至默认三星彩信封（rainbow）。
    /// </summary>
    /// <param name="s">原始学生条目。</param>
    /// <returns>已清洗名称与合法信封等级的实体对象。</returns>
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
