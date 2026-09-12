package com.lan.notify.data

import android.net.Uri

/**
 * Remote adapter rules may provide an official mini-program deep link. Keep
 * the accepted schemes narrow so a catalog update cannot launch an arbitrary
 * Android intent.
 */
fun safeActionUri(value: String): Uri? {
    val trimmed = value.trim()
    if (trimmed.isEmpty() || trimmed.length > 2048) return null

    val uri = runCatching { Uri.parse(trimmed) }.getOrNull() ?: return null
    return when (uri.scheme?.lowercase()) {
        "https" -> uri.takeIf { !it.host.isNullOrBlank() }
        "weixin" -> uri
        else -> null
    }
}
