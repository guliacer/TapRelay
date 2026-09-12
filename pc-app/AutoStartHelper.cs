using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace TapRelay
{
    /// <summary>
    /// 开机自启动助手：通过 HKCU 的 Run 键注册当前程序路径。
    /// 仅写当前用户注册表，不需要管理员权限；注册表损坏/被禁时静默降级。
    /// </summary>
    internal static class AutoStartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TapRelay";

        internal static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key == null)
                {
                    return;
                }

                if (enabled)
                {
                    key.SetValue(ValueName, CurrentExecutablePath());
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception)
            {
                // 注册表写入失败不应影响主流程。
            }
        }

        private static string CurrentExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
