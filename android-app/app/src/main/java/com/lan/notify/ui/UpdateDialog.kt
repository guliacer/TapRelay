package com.lan.notify.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.UpdateRelease
import com.lan.notify.ui.theme.appColors

@Composable
fun UpdateDialog(
    release: UpdateRelease,
    currentVersion: String,
    onOpenUpdate: () -> Unit,
    onSkipOnce: () -> Unit,
    onNeverPrompt: () -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Column {
                Text(
                    text = release.name.ifBlank { "TapRelay 有新版本" },
                    fontWeight = FontWeight.SemiBold
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    text = "当前版本 $currentVersion  ·  最新版本 ${release.displayVersion}",
                    color = appColors.TextSecondary,
                    fontSize = 12.sp
                )
            }
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 240.dp)
                    .verticalScroll(rememberScrollState())
                    .padding(vertical = 2.dp)
            ) {
                Text(
                    text = release.body.ifBlank { "本次更新未提供详细说明。" },
                    color = appColors.TextSecondary,
                    fontSize = 13.sp,
                    lineHeight = 20.sp
                )
            }
        },
        confirmButton = {
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                PlainButton(
                    text = "去更新",
                    onClick = onOpenUpdate,
                    modifier = Modifier.fillMaxWidth()
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    PlainButton(
                        text = "跳过本次",
                        onClick = onSkipOnce,
                        modifier = Modifier.weight(1f),
                        variant = PlainButtonVariant.SECONDARY
                    )
                    PlainButton(
                        text = "永久跳过",
                        onClick = onNeverPrompt,
                        modifier = Modifier.weight(1f),
                        variant = PlainButtonVariant.SECONDARY
                    )
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("关闭") }
        }
    )
}
