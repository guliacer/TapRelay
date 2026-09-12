using System;

namespace TapRelay
{
    /// <summary>
    /// Claude Code Haha 权限审批 requestId 的编解码。
    ///
    /// 手机端「允许 / 拒绝」必须带回一个能唯一定位 sidecar 会话与权限请求的
    /// 标识，因此格式固定为 <c>claude-haha:&lt;sessionId&gt;:&lt;permissionRequestId&gt;</c>。
    /// sessionId 为 UUID（不含冒号），permissionRequestId 由 sidecar 生成，
    /// 允许包含冒号，故解析时只在第一个冒号处切分。
    ///
    /// 单独成文件是为了让测试工程能只链接这一个无依赖类型；
    /// <see cref="ClaudeHahaPermissionBridge"/> 依赖 <c>NotifyServer</c>，
    /// 无法进入测试工程。
    /// </summary>
    internal static class ClaudeHahaApprovalRequestId
    {
        internal const string Prefix = "claude-haha:";

        internal static bool Owns(string? requestId) =>
            !string.IsNullOrWhiteSpace(requestId) &&
            requestId!.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        internal static string Build(string sessionId, string permissionRequestId) =>
            Prefix + sessionId + ":" + permissionRequestId;

        internal static bool TryParse(string? requestId, out string sessionId, out string permissionRequestId)
        {
            sessionId = string.Empty;
            permissionRequestId = string.Empty;
            if (!Owns(requestId))
            {
                return false;
            }

            var rest = requestId!.Substring(Prefix.Length);
            var separator = rest.IndexOf(':');
            if (separator <= 0 || separator >= rest.Length - 1)
            {
                return false;
            }

            sessionId = rest.Substring(0, separator).Trim();
            permissionRequestId = rest.Substring(separator + 1).Trim();
            return sessionId.Length > 0 && permissionRequestId.Length > 0;
        }
    }
}
