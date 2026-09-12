using System.Text.RegularExpressions;

namespace TapRelay
{
    /// <summary>
    /// Defense-in-depth masking for text that will be shown on a phone
    /// notification. Agents are expected to send already-sanitized
    /// <c>inputSummary</c> values, but approval commands and waiting prompts
    /// routinely embed credentials (API keys, tokens, passwords) in plain text.
    /// This class rewrites the most common secret shapes before broadcast so a
    /// lost/stolen phone screen never leaks a key.
    ///
    /// Ported from the risky-pattern masking used by tlive's approval cards.
    /// </summary>
    internal static class SecretMasker
    {
        private const string Hidden = "[已隐藏]";

        /// <summary>
        /// Ordered patterns. Each pattern captures the "prefix to preserve" in
        /// group 1 (e.g. <c>API_KEY=</c> or <c>export API_KEY=</c>); the secret
        /// itself is always dropped and replaced by [已隐藏]. Patterns without a
        /// group 1 mask the entire match. Order matters: env-var exports run
        /// before the generic key=value rule so the variable name survives.
        /// </summary>
        private static readonly Regex[] Patterns =
        {
            // export OPENAI_API_KEY=… / API_KEY=… (specific well-known names).
            new Regex(@"(?i)\b((?:export\s+)?(?:OPENAI_API_KEY|ANTHROPIC_API_KEY|GITHUB_TOKEN|AWS_SECRET_ACCESS_KEY|API_KEY|ACCESS_TOKEN|REFRESH_TOKEN|DB_PASSWORD)\s*=\s*)[^\s""'&|;]+", RegexOptions.Compiled),
            // key=value / key: value assignment of secrets (common in shell commands).
            new Regex(@"(?i)\b((?:api[_-]?key|token|secret|password|passwd|pwd|auth|credential)s?\s*[=:]\s*)[^\s""'&|;]{8,}", RegexOptions.Compiled),
            // OpenAI / Anthropic style keys (sk-…, sk-ant-…).
            new Regex(@"\bsk-[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled),
            // GitHub tokens (ghp_…, github_pat_…).
            new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{16,}\b", RegexOptions.Compiled),
            // AWS access keys (AKIA… / ASIA…).
            new Regex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled),
            // JWT (eyJ….….…).
            new Regex(@"\beyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled),
            // Bearer <token>.
            new Regex(@"(?i)\bBearer\s+[A-Za-z0-9_\-\.]{16,}\b", RegexOptions.Compiled),
            // Authorization: <token>.
            new Regex(@"(?i)\bAuthorization\s*[:=]\s*[A-Za-z0-9_\-\.]{16,}\b", RegexOptions.Compiled),
        };

        internal static string Mask(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var result = text;
            foreach (var pattern in Patterns)
            {
                result = pattern.Replace(result, match =>
                {
                    // Group 1 is the "key=" prefix that should stay visible so the
                    // user still sees *what* was hidden (not the secret value).
                    if (match.Groups.Count > 1 && match.Groups[1].Success)
                    {
                        return match.Groups[1].Value + Hidden;
                    }
                    return Hidden;
                });
            }
            return result;
        }
    }
}
