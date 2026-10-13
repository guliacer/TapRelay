using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TapRelay
{
    /// <summary>
    /// 安装签名：每个已适配应用"缺一个程序即不可用"的启动文件与必须文件清单。
    /// 安装证据（可信根 exe / 有效注册表）只证明"装过"，签名校验进一步证明
    /// "装得完整"——启动文件在但 resources\app.asar 等核心文件被删/损坏的
    /// 残缺安装，一律判为未安装（2026-10-13 ZCode 空目录与 OpenCode/Freebuff
    /// 残留数据的同源教训：目录或 exe 存在 ≠ 可用安装）。
    /// 必须文件只做存在性校验，不校验内容/版本/hash，避免升级中被占用
    /// 的文件被误判。签名表变更必须附"缺一个文件程序即不可用"的论证，
    /// 禁止把可选资源（custom_nodes、语言包、示例）列进必须清单。
    /// </summary>
    internal static class AppInstallSignature
    {
        /// <summary>
        /// 每个 appKey 的签名。Key 是相对安装根的路径；文件与目录均可
        /// （File.Exists / Directory.Exists 任一命中即算存在）。
        /// 未登记的 appKey（CLI 型 codex、自定义注册应用、素言源码形态）
        /// 返回 false，表示无签名约束、退回现有安装判定，避免误杀。
        /// </summary>
        private static readonly Dictionary<string, string[]> RequiredFiles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // Electron 桌面应用统一签名：启动 exe + resources\app.asar +
                // icudtl.dat + resources.pak。缺 app.asar 主程序逻辑不存在；
                // 缺 icudtl/resources.pak Chromium 壳无法启动。
                ["workbuddy"] = new[]
                {
                    "WorkBuddy.exe", "resources\\app.asar", "icudtl.dat", "resources.pak"
                },
                ["workbuddy-ai"] = new[]
                {
                    "WorkBuddyAI.exe", "resources\\app.asar", "icudtl.dat", "resources.pak"
                },
                ["claude-haha"] = new[]
                {
                    "Claude Code Haha.exe", "resources\\app.asar", "icudtl.dat", "resources.pak"
                },
                ["zcode"] = new[]
                {
                    "ZCode.exe", "resources\\app.asar", "icudtl.dat"
                },
                ["trae"] = new[]
                {
                    "resources\\app.asar", "icudtl.dat", "resources.pak"
                },
                ["traework"] = new[]
                {
                    "resources\\app.asar", "icudtl.dat", "resources.pak"
                },
                ["freebuff"] = new[]
                {
                    "Freebuff.exe", "resources\\app.asar", "icudtl.dat"
                },
                ["opencode"] = new[]
                {
                    "OpenCode.exe", "resources\\app.asar", "icudtl.dat"
                },
                ["deepseekharness"] = new[]
                {
                    "resources\\app.asar", "icudtl.dat"
                },
                ["adrive"] = new[]
                {
                    "aDrive.exe", "resources\\app.asar", "icudtl.dat"
                },
                // 豆包两级布局：外层 Doubao.exe 只是启动器，真主体在 app\ 子层
                // （Doubao.dll + resources.pak）。签名锁 app\ 本体，避开外层
                // app.backup.* 备份残留目录。
                ["doubao"] = new[]
                {
                    "app\\Doubao.exe", "app\\Doubao.dll", "app\\resources.pak"
                },
                // 百度网盘绿化版：CEF 单体布局，Host/Render 进程壳与核心 DLL
                // 缺任一即不可用。
                ["baidunetdisk"] = new[]
                {
                    "BaiduNetdisk.exe", "BaiduNetdiskHost.exe", "kernel.dll", "libcef.dll"
                },
                // 雷神：双进程（launcher + leigod.exe）+ Electron 资源。
                ["leigod"] = new[]
                {
                    "leigod_launcher.exe", "leigod.exe", "resources\\app.asar", "icudtl.dat"
                },
                // 素言打包形态（release\win-unpacked）。源码仓库形态
                // （W:\提示词）由 SuyanAppLocator 现有判定兜底，不在此登记。
                ["suyan"] = new[]
                {
                    "resources\\app.asar", "icudtl.dat"
                },
                // ComfyUI 官方便携版：python_embeded 解释器 + ComfyUI 主程序。
                // custom_nodes 是用户扩展目录，不必须。
                ["comfyui"] = new[]
                {
                    "python_embeded\\python.exe", "ComfyUI\\main.py", "ComfyUI\\server.py"
                }
            };

        /// <summary>
        /// 启动文件的可选组（OR 语义）：同一组内全部存在才算满足该组；任一组
        /// 满足即通过。素言的启动文件有中英文两种命名，任一存在即可；
        /// trae/traework 同理（官方更名后新旧 exe 名并存）。启动文件登记在
        /// 此处而非 RequiredFiles，避免"两个名字都要存在"的误判。
        /// </summary>
        private static readonly Dictionary<string, string[][]> AlternativeGroups =
            new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase)
            {
                ["suyan"] = new[]
                {
                    new[] { "素言.exe" },
                    new[] { "suyan.exe" }
                },
                ["trae"] = new[]
                {
                    new[] { "Trae CN.exe" },
                    new[] { "TraeCode CN.exe" }
                },
                ["traework"] = new[]
                {
                    new[] { "TRAE SOLO CN.exe" },
                    new[] { "TraeWork CN.exe" }
                },
                ["deepseekharness"] = new[]
                {
                    new[] { "DeepSeek Harness Desktop.exe" },
                    new[] { "DeepSeek Harness.exe" }
                }
            };

        /// <summary>
        /// 判断 appKey 是否有签名约束。无约束的应用退回现有安装判定
        /// （CLI 单文件形态、自定义注册应用没有"必须文件组合"可定义）。
        /// </summary>
        internal static bool HasSignature(string appKey)
        {
            var key = (appKey ?? string.Empty).Trim().ToLowerInvariant();
            return RequiredFiles.ContainsKey(key);
        }

        /// <summary>
        /// 校验安装根下签名是否齐全。无签名的 appKey 一律返回 true
        /// （无约束即不拦截）。夸克网盘主体在随升级变化的版本号子目录下，
        /// 走 <see cref="HasVersionedPayload"/> 通配谓词而非固定清单。
        /// </summary>
        internal static bool IsComplete(string appKey, string installRoot)
        {
            var key = (appKey ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length == 0 || string.IsNullOrWhiteSpace(installRoot))
            {
                return false;
            }

            // 夸克：根目录仅是壳+卸载器，真主体（icudtl.dat 等运行时）在
            // 版本号子目录里，版本号随升级变化，不能硬编码。
            if (key == "quarknetdisk")
            {
                return HasVersionedPayload(installRoot);
            }

            if (!RequiredFiles.TryGetValue(key, out var required))
            {
                // 无签名约束：不拦截，退回现有判定。
                return true;
            }

            foreach (var item in required)
            {
                if (!ExistsUnder(installRoot, item))
                {
                    return false;
                }
            }

            // 可选组：登记了的应用必须满足其中至少一组（如新旧 exe 命名）。
            if (AlternativeGroups.TryGetValue(key, out var groups))
            {
                var satisfied = groups.Any(group => group.All(item => ExistsUnder(installRoot, item)));
                if (!satisfied)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 夸克版本子目录谓词：根下任一直接子目录含 icudtl.dat 即主体在位。
        /// 只看一层，符合"不得全盘扫描"的目录探测上限。
        /// </summary>
        internal static bool HasVersionedPayload(string installRoot)
        {
            try
            {
                if (!Directory.Exists(installRoot)) return false;
                return Directory.EnumerateDirectories(installRoot)
                    .Any(child => File.Exists(Path.Combine(child, "icudtl.dat")));
            }
            catch
            {
                // 枚举受限按主体缺失处理。
                return false;
            }
        }

        private static bool ExistsUnder(string root, string relativePath)
        {
            try
            {
                var full = Path.Combine(root, relativePath);
                return File.Exists(full) || Directory.Exists(full);
            }
            catch
            {
                // 非法路径组合按缺失处理。
                return false;
            }
        }
    }
}
