using System;
using System.IO;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// 持久化的 PC 端轻量 UI 状态：
    /// - TestMessage：测试通知输入框文本（重启后仍保留，不回退默认值）
    /// - DarkMode：深/浅色主题（null 表示未设置过，回退旧 settings.json）
    /// - CompactMode：主窗口简洁视图（缺失时默认为标准视图）
    /// - SkippedUpdateVersion：用户暂时跳过的版本号
    /// - NeverPromptUpdates：用户永久关闭版本更新提醒
    /// </summary>
    internal sealed class UiState
    {
        public string TestMessage { get; set; } = string.Empty;
        public bool? DarkMode { get; set; }
        public bool CompactMode { get; set; }
        public string SkippedUpdateVersion { get; set; } = string.Empty;
        public bool NeverPromptUpdates { get; set; }
    }

    internal static class UiStateStore
    {
        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "ui-state.json");

        internal static UiState Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(StatePath));
                    if (state != null)
                    {
                        return state;
                    }
                }
            }
            catch
            {
                // 损坏/旧格式的状态文件不应阻止 PC UI 打开，回退到默认值。
            }
            return new UiState();
        }

        internal static void Save(UiState state)
        {
            try
            {
                var directory = Path.GetDirectoryName(StatePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(
                    StatePath,
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失败不应影响主流程。
            }
        }
    }
}
