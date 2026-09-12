package com.lan.notify.data

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.nio.charset.StandardCharsets

/** Endpoint carried by a TapRelay connection QR code. */
data class ConnectionEndpoint(
    val host: String,
    val port: Int,
    val scheme: String = "ws"
) {
    val authority: String
        get() = if (host.contains(":") && !host.startsWith("[")) {
            "[$host]:$port"
        } else {
            "$host:$port"
        }

    /** Full URL used to open the WebSocket connection (ws:// for LAN, wss:// for tunnel). */
    val url: String
        get() = "$scheme://$authority"
}

/**
 * Relay connection metadata extracted from a TapRelay QR code.
 * When present, the phone bypasses LAN endpoints and connects through
 * the relay server using a shared room ID.
 */
data class RelayConfig(
    /** Relay WebSocket URL (ws:// or wss://). */
    val url: String,
    /** Shared room identifier for pairing with the host PC. */
    val roomId: String
)

/**
 * Versioned enough to be extended later without making a QR code look like a
 * raw IP address. No password or authentication material is included.
 */
object ConnectionQrCodec {
    private const val SCHEME = "taprelay"
    private const val DEFAULT_PORT = 1122

    fun encode(host: String, port: Int): String {
        val endpoint = ConnectionEndpoint(normalizeHost(host), port)
        require(endpoint.host.isNotBlank()) { "host is blank" }
        require(endpoint.port in 1..65535) { "port is out of range" }
        return "$SCHEME://connect?host=${urlEncode(endpoint.host)}&port=${endpoint.port}"
    }

    /** Decode a TapRelay QR payload, or a raw ws:// / http:// endpoint for compatibility. */
    fun decode(raw: String): ConnectionEndpoint? {
        val value = raw.trim()
        if (value.isBlank()) return null

        val uri = runCatching { URI(value) }.getOrNull() ?: return null
        return when (uri.scheme?.lowercase()) {
            SCHEME -> {
                if (!uri.host.equals("connect", ignoreCase = true)) return null
                val query = parseQuery(uri.rawQuery.orEmpty())
                val host = query["host"]?.let(::urlDecode).orEmpty()
                val port = query["port"]?.toIntOrNull() ?: DEFAULT_PORT
                endpointOrNull(host, port, inferScheme(port))
            }
            "ws", "http", "wss" -> {
                val host = uri.host ?: return null
                val secure = uri.scheme.equals("wss", ignoreCase = true)
                val port = if (uri.port > 0) {
                    uri.port
                } else if (secure) {
                    443
                } else {
                    DEFAULT_PORT
                }
                endpointOrNull(host, port, if (secure) "wss" else "ws")
            }
            else -> null
        }
    }

    /**
     * Decode every candidate endpoint carried by a TapRelay QR, in connection-preference
     * order. The primary [host]/[port] is always first, followed by any `altN` entries
     * (each an escaped `host:port`). The phone tries them in order until one connects.
     * Returns an empty list for anything that is not a valid TapRelay payload.
     */
    fun decodeList(raw: String): List<ConnectionEndpoint> {
        val value = raw.trim()
        if (value.isBlank()) return emptyList()

        val uri = runCatching { URI(value) }.getOrNull() ?: return emptyList()
        if (!uri.scheme.equals(SCHEME, ignoreCase = true) ||
            !uri.host.equals("connect", ignoreCase = true)) {
            // Raw ws:// or http:// compatibility: a single endpoint at most.
            val single = decode(raw)
            return if (single != null) listOf(single) else emptyList()
        }

        val query = parseQuery(uri.rawQuery.orEmpty())
        val primaryHost = query["host"]?.let(::urlDecode).orEmpty()
        val primaryPort = query["port"]?.toIntOrNull() ?: DEFAULT_PORT
        val primary = endpointOrNull(primaryHost, primaryPort, inferScheme(primaryPort))
            ?: return emptyList()

        val result = mutableListOf(primary)
        var index = 1
        while (true) {
            val altRaw = query["alt$index"]?.let(::urlDecode) ?: break
            index++
            val endpoint = parseEndpoint(altRaw) ?: continue
            if (result.none { it.authority.equals(endpoint.authority, ignoreCase = true) }) {
                result += endpoint
            }
        }
        return result
    }

    /**
     * Extract optional relay metadata from a TapRelay QR payload.
     * When the QR was generated with relay enabled, the phone should connect
     * directly to the relay server instead of trying LAN/WAN endpoints.
     * Returns null when no relay metadata is present (pure-LAN QR).
     */
    fun extractRelay(raw: String): RelayConfig? {
        val value = raw.trim()
        if (value.isBlank()) return null
        val uri = runCatching { URI(value) }.getOrNull() ?: return null
        if (!uri.scheme.equals(SCHEME, ignoreCase = true)) return null
        val query = parseQuery(uri.rawQuery.orEmpty())
        val room = query["room"]?.let(::urlDecode).orEmpty()
        val relay = query["relay"]?.let(::urlDecode).orEmpty()
        if (room.isBlank() || relay.isBlank()) return null
        // Validate relay URL
        val relayUri = runCatching {
            var r = relay
            if (!r.startsWith("ws://", ignoreCase = true) &&
                !r.startsWith("wss://", ignoreCase = true)) {
                r = "ws://$r"
            }
            URI(r)
        }.getOrNull() ?: return null
        val scheme = if (relayUri.scheme.equals("wss", ignoreCase = true)) "wss" else "ws"
        val port = if (relayUri.port > 0) relayUri.port else if (scheme == "wss") 443 else DEFAULT_PORT
        val host = relayUri.host ?: return null
        if (host.isBlank()) return null
        return RelayConfig(
            url = "$scheme://$host:$port",
            roomId = room
        )
    }

    /** 主地址（host/port 不带 scheme 信息）：443 推断为 wss 公网隧道，其余默认 ws。 */
    private fun inferScheme(port: Int): String = if (port == 443) "wss" else "ws"

    /**
     * Parse an endpoint string with an optional scheme prefix:
     *   "host:port" -> ws://host:port
     *   "host"      -> ws://host:1122
     *   "wss://host"-> wss://host:443 (Cloudflare 隧道)
     *   "ws://host" -> ws://host:1122
     */
    private fun parseEndpoint(raw: String): ConnectionEndpoint? {
        var value = raw.trim()
        if (value.isBlank()) return null

        var scheme = "ws"
        val prefix = listOf("wss://", "ws://", "https://", "http://").firstOrNull {
            value.startsWith(it, ignoreCase = true)
        }
        if (prefix != null) {
            scheme = if (prefix.equals("wss://", ignoreCase = true) ||
                prefix.equals("https://", ignoreCase = true)) "wss" else "ws"
            value = value.substring(prefix.length).trim()
        }
        if (value.isBlank()) return null

        if (value.startsWith("[") && value.contains("]")) {
            val end = value.indexOf(']')
            val host = value.substring(1, end)
            val suffix = value.substring(end + 1)
            val port = suffix.removePrefix(":").toIntOrNull()
                ?: if (scheme == "wss") 443 else DEFAULT_PORT
            return endpointOrNull(host, port, scheme)
        }

        val colon = value.lastIndexOf(':')
        if (colon > 0 && value.indexOf(':') == colon) {
            val port = value.substring(colon + 1).toIntOrNull()
            if (port != null && port in 1..65535) {
                return endpointOrNull(value.substring(0, colon), port, scheme)
            }
        }
        return endpointOrNull(value, if (scheme == "wss") 443 else DEFAULT_PORT, scheme)
    }


    /** Normalize the editable address field while preserving an optional :port. */
    fun normalizeHost(input: String): String {
        var value = input.trim()
        if (value.isBlank()) return ""
        listOf("ws://", "wss://", "http://", "https://").firstOrNull {
            value.startsWith(it, ignoreCase = true)
        }?.let { value = value.substring(it.length) }
        value = value.substringBefore('/').trim()
        if (value.equals("localhost", ignoreCase = true) || value == "127.0.0.1") return ""

        // Keep bracketed IPv6 authorities intact.
        if (value.startsWith("[") && value.contains("]")) {
            val end = value.indexOf(']')
            val host = value.substring(1, end)
            val suffix = value.substring(end + 1)
            val port = suffix.removePrefix(":").toIntOrNull()
            return if (port != null && port in 1..65535) ConnectionEndpoint(host, port).authority else host
        }

        val colon = value.lastIndexOf(':')
        if (colon > 0 && value.indexOf(':') == colon) {
            val port = value.substring(colon + 1).toIntOrNull()
            if (port != null && port in 1..65535) {
                return ConnectionEndpoint(value.substring(0, colon), port).authority
            }
            // "foo:", "foo:abc" or port out of range are not valid authorities.
            return ""
        }
        return value
    }

    private fun endpointOrNull(host: String, port: Int, scheme: String = "ws"): ConnectionEndpoint? {
        val normalized = normalizeHost(host)
        if (normalized.isBlank() || port !in 1..65535 ||
            normalized.any { it.isWhitespace() }) return null
        val authority = normalized
        val split = authority.lastIndexOf(':')
        return if (split > 0 && authority.indexOf(':') == split &&
            authority.substring(split + 1).toIntOrNull() != null) {
            ConnectionEndpoint(authority.substring(0, split), authority.substring(split + 1).toInt(), scheme)
        } else {
            ConnectionEndpoint(authority.removePrefix("[").removeSuffix("]"), port, scheme)
        }
    }

    private fun parseQuery(rawQuery: String): Map<String, String> =
        rawQuery.split('&')
            .asSequence()
            .mapNotNull { item ->
                val separator = item.indexOf('=')
                if (separator <= 0) return@mapNotNull null
                item.substring(0, separator) to item.substring(separator + 1)
            }
            .toMap()

    private fun urlEncode(value: String): String =
        URLEncoder.encode(value, StandardCharsets.UTF_8.name())

    private fun urlDecode(value: String): String =
        URLDecoder.decode(value, StandardCharsets.UTF_8.name())
}
