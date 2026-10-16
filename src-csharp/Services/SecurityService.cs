// ==============================================================================
// 文件: SecurityService.cs
// 命名空间: BlueRandom.Services
// 作用: 安全防护与设置密码控制服务。
//       采用 SHA-256 哈希加固与 CryptographicOperations.FixedTimeEquals 恒定时间比对，
//       杜绝基于时序攻击（Timing Attack）的密码爆破风险，并提供安全恢复出厂重置能力。
// ==============================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// 安全管理与密码保护服务 (.SHA256)。
/// </summary>
public static class SecurityService
{
    /// <summary>
    /// 获取密码哈希特征文件的绝对物理路径（~/.blue random/.SHA256）。
    /// </summary>
    /// <returns>密码保护文件路径。</returns>
    private static string GetSecurityFilePath()
    {
        return Path.Combine(YamlConfigService.GetUserDataDir(), ".SHA256");
    }

    /// <summary>
    /// 检查当前是否已启用管理密码锁定保护。
    /// </summary>
    /// <returns>已设密且文件非空返回 true，否则返回 false。</returns>
    public static bool IsEnabled()
    {
        string path = GetSecurityFilePath();
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>
    /// 设置并哈希持久化新的管理密码。
    /// 要求密码长度至少为 4 个字符。
    /// </summary>
    /// <param name="password">明文密码字符串。</param>
    /// <returns>设置成功返回 true，长度不足或 IO 失败返回 false。</returns>
    public static bool SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 4) return false;
        try
        {
            string hash = ComputeSha256(password);
            File.WriteAllText(GetSecurityFilePath(), hash, Encoding.UTF8);
            AppLogger.Info("Security", "密码已成功设定");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Security", "设定密码失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 校验用户输入的密码是否与已持久化的哈希特征匹配。
    /// <para>
    /// 安全设计：
    /// 1. 若当前未启用密码保护（IsEnabled = false），免密直接放行返回 true；
    /// 2. 校验比对过程采用 <c>CryptographicOperations.FixedTimeEquals</c> 恒定时间字节比对，
    ///    防止侧信道时序攻击（Timing Attack）推测哈希内容。
    /// </para>
    /// </summary>
    /// <param name="password">待验证的输入明文。</param>
    /// <returns>验证通过返回 true，否则返回 false。</returns>
    public static bool VerifyPassword(string password)
    {
        if (!IsEnabled()) return true;
        try
        {
            string storedHash = File.ReadAllText(GetSecurityFilePath(), Encoding.UTF8).Trim();
            string inputHash = ComputeSha256(password ?? "");
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(storedHash),
                Encoding.UTF8.GetBytes(inputHash));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Security", "验证密码异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 解除管理密码保护。
    /// 前提必须成功校验传入的原密码，校验通过后物理删除 .SHA256 文件。
    /// </summary>
    /// <param name="password">现有原密码。</param>
    /// <returns>密码匹配并成功删除返回 true，原密码错误或 IO 失败返回 false。</returns>
    public static bool DisablePassword(string password)
    {
        if (!VerifyPassword(password)) return false;
        try
        {
            string path = GetSecurityFilePath();
            if (File.Exists(path)) File.Delete(path);
            AppLogger.Info("Security", "密码保护已解除");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Security", "解除密码失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 强制全量重置应用所有配置与存储（出厂重置）。
    /// <para>
    /// 执行动作：
    /// 1. 物理删除 .SHA256 密码文件；
    /// 2. 将 config.yaml 覆写为内置默认配置；
    /// 3. 递归清空 classes/ 班级花名册目录；
    /// 4. 递归清空 customs/ 自定义素材目录。
    /// </para>
    /// </summary>
    /// <returns>全量清理成功返回 true，出现异常返回 false。</returns>
    public static bool ResetAllConfig()
    {
        try
        {
            // 1. 删除密码
            string secPath = GetSecurityFilePath();
            if (File.Exists(secPath)) File.Delete(secPath);

            // 2. 重置主配置
            YamlConfigService.WriteDefaultConfig(YamlConfigService.GetConfigFilePath());

            // 3. 清理班级与自定义资源
            string classesDir = ClassManagerService.GetClassesDir();
            if (Directory.Exists(classesDir)) Directory.Delete(classesDir, true);

            string customsDir = CustomsStore.GetCustomsDir();
            if (Directory.Exists(customsDir)) Directory.Delete(customsDir, true);

            AppLogger.Info("Security", "已全量重置所有配置、名单与自定义素材");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Security", "重置所有配置失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 计算指定文本的 SHA-256 散列哈希值。
    /// </summary>
    /// <param name="text">输入明文字符串。</param>
    /// <returns>64 位小写十六进制哈希摘要字符串。</returns>
    private static string ComputeSha256(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
