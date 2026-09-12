using System;
using System.Collections.Generic;

namespace TapRelay
{
    /// <summary>
    /// 内置「已适配应用」目录的静态元数据：稳定 appKey、用户可见名称与
    /// 鼠标悬浮时展示的功能说明。显示名称与图标仍复用在
    /// <c>NotificationAppsPanel</c> 中已有的逻辑（codex→ChatGPT 等映射）。
    /// </summary>
    internal static class AppCatalogMetadata
    {
        /// <summary>
        /// 内置已适配应用的稳定键。提交给用户展示区时保持此顺序，
        /// 已安装分区在前、未安装分区在后各按此顺序稳定排列。
        /// </summary>
        internal static readonly IReadOnlyList<string> BuiltInKeys = new[]
        {
            "claude",
            "claude-haha",
            "codex",
            "trae",
            "traework",
            "zcode",
            "doubao",
            "suyan",
            "baidunetdisk",
            "quarknetdisk",
            "adrive",
            "workbuddy",
            "comfyui",
            "opencode",
            "deepseekharness",
            "freebuff",
            "leigod"
        };

        /// <summary>
        /// 返回某 appKey 的功能说明；未收录的键返回空字符串。
        /// </summary>
        internal static string GetTooltipText(string appKey)
        {
            switch ((appKey ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "claude":
                case "claude-haha":
                case "codex":
                case "trae":
                case "traework":
                case "zcode":
                    return "完成/失败/等待确认时通知";
                case "doubao":
                case "workbuddy":
                case "opencode":
                case "deepseekharness":
                    return "完成/失败时通知";
                case "freebuff":
                    return "完成/失败/等待确认时通知";
                case "suyan":
                    return "生图完成/失败时通知";
                case "baidunetdisk":
                case "quarknetdisk":
                case "adrive":
                    return "上传/下载完成时通知";
                case "comfyui":
                    return "配合 ComfyUI-GuliNodes，工作流结束时通知";
                case "leigod":
                    return "关闭时提醒是否已停用加速";
                default:
                    return string.Empty;
            }
        }
    }
}