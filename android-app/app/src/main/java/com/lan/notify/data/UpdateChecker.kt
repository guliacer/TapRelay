package com.lan.notify.data

import com.lan.notify.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class UpdateRelease(
    val version: String,
    val displayVersion: String,
    val name: String,
    val body: String,
    val releaseUrl: String,
    val downloadUrl: String
) {
    val targetUrl: String
        get() = downloadUrl.ifBlank { releaseUrl }
}

object UpdateChecker {
    private const val LATEST_RELEASE_URL =
        "https://api.github.com/repos/guliacer/TapRelay/releases/latest"
    private val httpClient = OkHttpClient.Builder()
        .callTimeout(6, TimeUnit.SECONDS)
        .build()

    val currentVersion: String
        get() = normalizeVersion(parseVersion(BuildConfig.VERSION_NAME) ?: Version(0, 0, 0))

    suspend fun fetchLatest(): UpdateRelease? = withContext(Dispatchers.IO) {
        runCatching {
            val request = Request.Builder()
                .url(LATEST_RELEASE_URL)
                .header("Accept", "application/vnd.github+json")
                .header("X-GitHub-Api-Version", "2022-11-28")
                .header("User-Agent", "TapRelay-Android/$currentVersion")
                .build()

            httpClient.newCall(request).execute().use { response ->
                if (!response.isSuccessful) return@use null
                val root = JSONObject(response.body.string())
                val rawTag = root.optString("tag_name").trim()
                val latest = parseVersion(rawTag) ?: return@use null
                val current = parseVersion(currentVersion) ?: Version(0, 0, 0)
                if (latest <= current) return@use null

                val releaseUrl = validHttpUrl(root.optString("html_url"))
                    ?: return@use null
                val downloadUrl = findAndroidAsset(root)
                UpdateRelease(
                    version = normalizeVersion(latest),
                    displayVersion = if (rawTag.startsWith("v", ignoreCase = true)) {
                        rawTag
                    } else {
                        "v" + normalizeVersion(latest)
                    },
                    name = root.optString("name").trim().ifBlank { "TapRelay 更新" },
                    body = root.optString("body").trim(),
                    releaseUrl = releaseUrl,
                    downloadUrl = downloadUrl
                )
            }
        }.getOrNull()
    }

    private fun findAndroidAsset(root: JSONObject): String {
        val assets = root.optJSONArray("assets") ?: return ""
        for (index in 0 until assets.length()) {
            val asset = assets.optJSONObject(index) ?: continue
            val name = asset.optString("name")
            if (!name.endsWith(".apk", ignoreCase = true)) continue
            validHttpUrl(asset.optString("browser_download_url"))?.let { return it }
        }
        return ""
    }

    private fun validHttpUrl(raw: String): String? {
        val value = raw.trim()
        return if (value.startsWith("https://", ignoreCase = true) ||
            value.startsWith("http://", ignoreCase = true)
        ) value else null
    }

    private data class Version(val major: Int, val minor: Int, val patch: Int) : Comparable<Version> {
        override fun compareTo(other: Version): Int =
            compareValuesBy(this, other, Version::major, Version::minor, Version::patch)
    }

    private fun parseVersion(raw: String): Version? {
        var candidate = raw.trim().trimStart('v', 'V')
        val separator = candidate.indexOfAny(charArrayOf('-', '+'))
        if (separator >= 0) candidate = candidate.substring(0, separator)
        val parts = candidate.split('.')
        if (parts.isEmpty() || parts.size > 3) return null
        val numbers = parts.map { it.toIntOrNull() ?: return null }
        if (numbers.any { it < 0 }) return null
        return Version(
            major = numbers.getOrElse(0) { 0 },
            minor = numbers.getOrElse(1) { 0 },
            patch = numbers.getOrElse(2) { 0 }
        )
    }

    private fun normalizeVersion(version: Version): String =
        "${version.major}.${version.minor}.${version.patch}"
}
