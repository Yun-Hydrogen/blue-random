using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BlueRandom.Core.Logging;

namespace BlueRandom.Services;

/// <summary>
/// 安全管理与密码保护服务 (.SHA256)
/// </summary>
public static class SecurityService
{
    private static string GetSecurityFilePath()
    {
        return Path.Combine(YamlConfigService.GetUserDataDir(), ".SHA256");
    }

    public static bool IsEnabled()
    {
        string path = GetSecurityFilePath();
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

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

    private static string ComputeSha256(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
