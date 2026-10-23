package com.lan.notify.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import com.lan.notify.R
import com.lan.notify.ui.theme.appColors

@Composable
fun ComfyUiIconButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    tint: Color? = null
) {
    val haptic = LocalHapticFeedback.current
    Box(
        modifier = modifier
            .size(28.dp)
            .clickable {
                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                onClick()
            },
        contentAlignment = Alignment.Center
    ) {
        Icon(
            painter = painterResource(R.drawable.ic_stat_comfyui),
            contentDescription = "ComfyUI",
            modifier = Modifier.size(20.dp),
            tint = tint ?: appColors.PrimarySage
        )
    }
}
