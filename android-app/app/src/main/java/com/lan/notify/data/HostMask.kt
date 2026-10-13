package com.lan.notify.data

/**
 * IPv4 / 域名脱敏：保留每段首字符，其余替换为 `*`。
 * - `192.168.5.35` -> `1**.1**.5.3*`
 * - `10.0.2.2` -> `1*.0.2.2`（单字符段没有可隐藏的字符，原样保留）
 * - 非 IPv4 格式（段数 != 4 或含非数字段）原样返回，避免误处理用户输入中的非 IP 文本。
 *
 * 放在 `data/` 而不是 `ui/`：`03-目录与架构规范.md` R4 只允许
 * `ui -> data`、`service -> data`、`service -> notification`，**禁止 `service -> ui`**。
 * 前台通知（`WebSocketService`）同样需要脱敏，所以这个纯字符串函数必须待在 `data` 层。
 * 同层已有 `MessageTimeFormatter` 作为「data 层放展示格式化函数」的先例。
 */
internal fun maskIp(input: String): String {
    val trimmed = input.trim()
    if (trimmed.isEmpty()) return input
    // 分离 scheme 前缀（ws:// / wss://）与端口后缀（:443），分别保留。
    val scheme = Regex("^([a-z][a-z0-9+.-]*://)").find(trimmed)?.value.orEmpty()
    val noScheme = if (scheme.isNotEmpty()) trimmed.removePrefix(scheme) else trimmed
    val portSuffix = Regex("(:\\d+)$").find(noScheme)?.value.orEmpty()
    val hostPart = if (portSuffix.isNotEmpty()) noScheme.removeSuffix(portSuffix) else noScheme
    val segments = hostPart.split(".")

    val isIpv4 = segments.size == 4 && segments.all { it.toIntOrNull() != null }
    val isDomain = !isIpv4 && segments.size >= 2 &&
        segments.all { Regex("^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?$").matches(it) } &&
        segments.last().any { it.isLetter() }
    if (!isIpv4 && !isDomain) return input

    val masked = segments.joinToString(".") { segment ->
        if (segment.isEmpty()) "*" else segment.first() + "*".repeat(segment.length - 1)
    }
    return scheme + masked + portSuffix
}

/**
 * 把文案中出现的 [host] 替换为脱敏形式。
 *
 * 用于**无法逐段拼装文案**的场景——典型是前台通知：文案形如
 * `已连接：192.168.5.6:1122`、`连接中 (1/2)：192.168.5.6:1122`，
 * 地址被拼进整句里，只能事后按原文替换。
 *
 * [host] 为空、或文案里根本不含它时原样返回；[maskIp] 判定为非地址文本时替换为恒等，也不会破坏文案。
 *
 * 做成纯函数是为了能单测：`WebSocketService` 里只剩接线，没有可测逻辑。
 */
internal fun maskHostInText(text: String, host: String?): String {
    if (host.isNullOrBlank() || !text.contains(host)) return text
    return text.replace(host, maskIp(host))
}
