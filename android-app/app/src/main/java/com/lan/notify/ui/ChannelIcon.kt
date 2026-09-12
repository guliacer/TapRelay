package com.lan.notify.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import com.lan.notify.R
import com.lan.notify.data.Channel

/** Brand icon used consistently in history rows, project headers, overlays and alerts. */
@Composable
fun ChannelIcon(
    channel: Channel,
    modifier: Modifier = Modifier.size(32.dp),
    contentDescription: String? = channel.label
) {
    Image(
        painter = painterResource(channelIconRes(channel)),
        contentDescription = contentDescription,
        contentScale = ContentScale.Fit,
        modifier = modifier
    )
}

fun channelIconRes(channel: Channel): Int = when (channel) {
    Channel.Claude -> R.drawable.ic_claude_logo
    Channel.ClaudeHaha -> R.drawable.ic_claude_haha_logo
    Channel.ChatGPT -> R.drawable.ic_chatgpt_logo
    Channel.Trae -> R.drawable.ic_trae_logo
    Channel.TraeWork -> R.drawable.ic_trae_solo_logo
    Channel.ZCode -> R.drawable.ic_zcode_logo
    Channel.Doubao -> R.drawable.ic_doubao_logo
    Channel.Qwen -> R.drawable.ic_qwen_logo
    Channel.Suyan -> R.drawable.ic_suyan_logo
    Channel.BaiduNetdisk -> R.drawable.ic_baidunetdisk_logo
    Channel.QuarkNetdisk -> R.drawable.ic_quarknetdisk_logo
    Channel.Adrive -> R.drawable.ic_adrive_logo
    Channel.WorkBuddy -> R.drawable.ic_workbuddy_logo
    Channel.ComfyUI -> R.drawable.ic_comfyui_logo
    Channel.Opencode -> R.drawable.ic_opencode_logo
    Channel.DeepseekHarness -> R.drawable.ic_deepseekharness_logo
    Channel.Freebuff -> R.drawable.ic_freebuff_logo
    Channel.LinuxDoCdk -> R.drawable.ic_linuxdo_cdk_logo
    Channel.TapRelay -> R.drawable.ic_taprelay_logo
    Channel.Unknown -> R.drawable.ic_taprelay_logo
}
