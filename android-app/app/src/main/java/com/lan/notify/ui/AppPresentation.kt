package com.lan.notify.ui

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.foundation.Image
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import com.lan.notify.data.Channel
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.canonicalSourceKey

data class AppPresentation(
    val key: String,
    val label: String,
    val color: Color,
    val channel: Channel
)

fun appPresentation(source: String, registeredApps: Map<String, RegisteredApp>): AppPresentation {
    val key = canonicalSourceKey(source)
    val channel = Channel.from(source)
    val registered = registeredApps[key]
    if (registered == null) {
        return AppPresentation(channel.name, channel.label, channel.color, channel)
    }

    val color = runCatching { Color(android.graphics.Color.parseColor(registered.brandColor)) }
        .getOrDefault(channel.color)
    return AppPresentation(key, registered.displayName, color, channel)
}

@Composable
fun AppIcon(
    source: String,
    registeredApps: Map<String, RegisteredApp>,
    modifier: Modifier,
    contentDescription: String? = null
) {
    val presentation = appPresentation(source, registeredApps)
    val registered = registeredApps[presentation.key]
    val bitmap = remember(registered?.iconBase64) {
        registered?.iconBase64
            ?.takeIf { it.isNotBlank() }
            ?.let { encoded ->
                runCatching {
                    val bytes = Base64.decode(encoded, Base64.DEFAULT)
                    BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
                }.getOrNull()
            }
    }

    if (bitmap != null) {
        Image(
            bitmap = bitmap,
            contentDescription = contentDescription ?: presentation.label,
            contentScale = ContentScale.Fit,
            modifier = modifier.clip(CircleShape)
        )
    } else {
        ChannelIcon(
            channel = presentation.channel,
            modifier = modifier,
            contentDescription = contentDescription ?: presentation.label
        )
    }
}
