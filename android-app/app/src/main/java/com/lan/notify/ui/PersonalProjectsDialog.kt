package com.lan.notify.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.ui.theme.DarkAppColors
import com.lan.notify.ui.theme.appColors

internal data class PersonalProject(
    val title: String,
    val domain: String,
    val url: String,
    val description: String,
    val tags: List<String>
)

// 与 W:\中转站推荐网页 的置顶个人项目快照保持一致（11 个，按原顺序）。
private val personalProjects = listOf(
    PersonalProject(
        title = "SuYan",
        domain = "github.com",
        url = "https://github.com/guliacer/SuYan",
        description = "为 AI 创作者整理、复用和打磨提示词而生。",
        tags = listOf("个人项目", "提示词")
    ),
    PersonalProject(
        title = "ComfyUI-GuliNodes",
        domain = "github.com",
        url = "https://github.com/guliacer/ComfyUI-GuliNodes",
        description = "ComfyUI 工具合集，囊括多种有用节点与工具。",
        tags = listOf("个人项目", "ComfyUI", "节点扩展")
    ),
    PersonalProject(
        title = "Prompt Polish 提示词优化器",
        domain = "github.com",
        url = "https://github.com/guliacer/prompt-polish",
        description = "轻量级桌面提示词优化器，完整整合提示词润色功能。",
        tags = listOf("个人项目", "提示词优化", "桌面工具")
    ),
    PersonalProject(
        title = "GetPhoto 图像提取",
        domain = "github.com",
        url = "https://github.com/guliacer/GetPhoto",
        description = "油猴插件，适配多站点，支持小红书 AI 爬图。",
        tags = listOf("个人项目", "油猴插件", "图像提取")
    ),
    PersonalProject(
        title = "PagePurifier 网页优化",
        domain = "github.com",
        url = "https://github.com/guliacer/PagePurifier",
        description = "油猴插件，拉黑 B 站广告 UP，精简网页广告。",
        tags = listOf("个人项目", "油猴插件", "网页净化")
    ),
    PersonalProject(
        title = "Preview 网页图像悬浮工具",
        domain = "github.com",
        url = "https://github.com/guliacer/preview",
        description = "油猴插件，鼠标悬停即可查看图像大图。",
        tags = listOf("个人项目", "油猴插件", "图像预览")
    ),
    PersonalProject(
        title = "VeilReader 网页摸鱼小说工具",
        domain = "github.com",
        url = "https://github.com/guliacer/VeilReader",
        description = "油猴插件，摸鱼的钱才是你赚到的钱。",
        tags = listOf("个人项目", "油猴插件", "摸鱼阅读")
    ),
    PersonalProject(
        title = "CookClick 网页美化",
        domain = "github.com",
        url = "https://github.com/guliacer/cookclick",
        description = "油猴插件，点击网页随机生成符号。",
        tags = listOf("个人项目", "油猴插件", "网页美化")
    ),
    PersonalProject(
        title = "PixGo",
        domain = "github.com",
        url = "https://github.com/guliacer/PixGo",
        description = "改善索尼相机无线传图。",
        tags = listOf("个人项目", "软件", "无线传图")
    ),
    PersonalProject(
        title = "新闻 HTML 日报",
        domain = "github.com",
        url = "https://github.com/guliacer/news-html-digest",
        description = "Skill 脚本，抓取新闻，消除信息差。",
        tags = listOf("个人项目", "Skill", "新闻抓取")
    )
)

private const val GITHUB_MARK_PATH =
    "M12,3.5 C7.3,3.5 3.5,7.3 3.5,12 C3.5,15.7 5.9,18.8 9.2,19.9 " +
        "M14.8,19.9 C18.1,18.8 20.5,15.7 20.5,12 C20.5,7.3 16.7,3.5 12,3.5 " +
        "M8.5,18 C8.5,16.5 9.2,15.5 10.2,15 " +
        "C8,14.7 6.6,13.6 6.6,11.2 C6.6,10.1 7,9.2 7.7,8.5 " +
        "C7.5,7.8 7.5,7.1 7.9,6.4 C8.9,6.5 9.7,6.9 10.3,7.3 " +
        "C11.4,7 12.6,7 13.7,7.3 C14.3,6.9 15.1,6.5 16.1,6.4 " +
        "C16.5,7.1 16.5,7.8 16.3,8.5 C17,9.2 17.4,10.1 17.4,11.2 " +
        "C17.4,13.6 16,14.7 13.8,15 C14.8,15.5 15.5,16.5 15.5,18"

private const val EXTERNAL_LINK_PATH =
    "M14,3 L21,3 L21,10 M21,3 L11,13 M18,13 L18,19 " +
        "C18,20.1 17.1,21 16,21 L5,21 C3.9,21 3,20.1 3,19 L3,8 " +
        "C3,6.9 3.9,6 5,6 L11,6"

private const val GLOBE_PATH =
    "M12,3 C7,3 3,7 3,12 C3,17 7,21 12,21 C17,21 21,17 21,12 " +
        "C21,7 17,3 12,3 Z M3,12 L21,12 " +
        "M12,3 C14.2,5.4 15.5,8.5 15.5,12 C15.5,15.5 14.2,18.6 12,21 " +
        "M12,3 C9.8,5.4 8.5,8.5 8.5,12 C8.5,15.5 9.8,18.6 12,21"

private const val CLOSE_PATH = "M6,6 L18,18 M18,6 L6,18"

private val GitHubIcon = ImageVector.Builder(
    name = "GitHub",
    defaultWidth = 24.dp,
    defaultHeight = 24.dp,
    viewportWidth = 24f,
    viewportHeight = 24f
).apply {
    addPath(
        pathData = PathParser().parsePathString(GITHUB_MARK_PATH).toNodes(),
        pathFillType = PathFillType.NonZero,
        name = "GitHub",
        fill = null,
        stroke = SolidColor(Color.Black),
        strokeLineWidth = 1.5f,
        strokeLineCap = StrokeCap.Round,
        strokeLineJoin = StrokeJoin.Round
    )
}.build()

private val ExternalLinkIcon = pixGoLineIcon("ExternalLink", EXTERNAL_LINK_PATH)
private val GlobeIcon = pixGoLineIcon("Globe", GLOBE_PATH)
private val CloseIcon = pixGoLineIcon("Close", CLOSE_PATH)

@Composable
fun PersonalProjectsIconButton(
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
            imageVector = GitHubIcon,
            contentDescription = "个人项目",
            modifier = Modifier.size(20.dp),
            tint = tint ?: appColors.PrimarySage
        )
    }
}

@Composable
fun PersonalProjectsDialog(onDismiss: () -> Unit) {
    val uriHandler = LocalUriHandler.current
    val context = LocalContext.current
    var failedProjectTitle by rememberSaveable { mutableStateOf<String?>(null) }

    fun openProject(project: PersonalProject) {
        failedProjectTitle = runCatching {
            uriHandler.openUri(project.url)
        }.fold(onSuccess = { null }, onFailure = { project.title })
    }

    fun copyProjectUrl(project: PersonalProject) {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText(project.title, project.url))
        Toast.makeText(context, "已复制 ${project.title} 地址", Toast.LENGTH_SHORT).show()
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = {
            Icon(
                imageVector = GitHubIcon,
                contentDescription = null,
                tint = appColors.PrimarySage
            )
        },
        title = {
            Text(
                text = "个人项目",
                fontWeight = FontWeight.Bold,
                color = appColors.TextPrimary
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    text = "自研节点、脚本与小工具。",
                    color = appColors.TextSecondary
                )
                Text(
                    text = "${personalProjects.size} 个项目 · 点击查看 GitHub",
                    color = appColors.PrimarySageStrong,
                    fontWeight = FontWeight.Bold
                )
                failedProjectTitle?.let { title ->
                    Text(
                        text = "无法打开 $title，请检查浏览器。",
                        color = appColors.Danger
                    )
                }
                LazyVerticalGrid(
                    columns = GridCells.Fixed(1),
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 480.dp),
                    horizontalArrangement = Arrangement.spacedBy(20.dp),
                    verticalArrangement = Arrangement.spacedBy(20.dp)
                ) {
                    items(personalProjects, key = { it.url }) { project ->
                        PersonalProjectCard(
                            project = project,
                            index = personalProjects.indexOf(project),
                            onOpen = { openProject(project) },
                            onCopyUrl = { copyProjectUrl(project) }
                        )
                    }
                }
            }
        },
        confirmButton = {
            Box(
                modifier = Modifier.fillMaxWidth(),
                contentAlignment = Alignment.Center
            ) {
                val haptic = LocalHapticFeedback.current
                Box(
                    modifier = Modifier
                        .size(28.dp)
                        .clip(CircleShape)
                        .clickable {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            onDismiss()
                        },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = CloseIcon,
                        contentDescription = "关闭",
                        modifier = Modifier.size(18.dp),
                        tint = appColors.PrimarySage
                    )
                }
            }
        },
        // 弹窗背景用 Background（更深的灰/深色），卡片用 Surface（纯白/次深），
        // 与首页历史消息列表的层次一致：Background 上放 ListItem surface，
        // 让卡片和弹窗背景有明确区分，而不是之前两者都是白色/都是深色分不开。
        containerColor = appColors.Background
    )
}

/**
 * 项目卡片：参照 中转站推荐网页 的 ResourceCard 视觉结构。
 * - 顶部 header：标题，带左侧主题色 accent 条
 * - 正文：描述 + 胶囊形标签（单排横向滚动，小字号）
 * - 底部："直达"主按钮打开 GitHub + "复制"按钮复制 URL
 */
@Composable
private fun PersonalProjectCard(
    project: PersonalProject,
    index: Int,
    onOpen: () -> Unit,
    onCopyUrl: () -> Unit
) {
    val haptic = LocalHapticFeedback.current
    val tone = projectCardTone(index)
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 164.dp)
            // 对齐素言 PromptCard：1px 主题色描边（rounded-xl + border）
            .border(1.dp, tone.border, RoundedCornerShape(12.dp)),
        shape = RoundedCornerShape(12.dp),
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp),
        colors = CardDefaults.cardColors(
            containerColor = appColors.Surface,
            contentColor = appColors.TextPrimary
        )
    ) {
        Column {
            // 顶部 header：主题色 accent 条 + 标题 + 域名，与网页端 card-header 对齐
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(tone.header)
                    .padding(start = 14.dp, end = 14.dp, top = 11.dp, bottom = 11.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(width = 4.dp, height = 34.dp)
                        .clip(RoundedCornerShape(2.dp))
                        .background(tone.border)
                )
                Spacer(Modifier.width(10.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = project.title,
                        fontWeight = FontWeight.SemiBold,
                        color = tone.foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Spacer(Modifier.width(8.dp))
                Icon(
                    imageVector = GitHubIcon,
                    contentDescription = null,
                    modifier = Modifier.size(18.dp),
                    tint = tone.foreground.copy(alpha = 0.8f)
                )
            }
            // header 底部描边（对齐素言 border-b）
            HorizontalDivider(color = tone.border.copy(alpha = 0.6f))
            Column(
                modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 12.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Text(
                    text = project.description,
                    color = appColors.TextSecondary,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                // 胶囊形标签：单排横向滚动，字号缩小，避免折行
                Row(
                    modifier = Modifier.horizontalScroll(rememberScrollState()),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    project.tags.forEach { tag ->
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(50))
                                .background(tone.tag)
                                .border(1.dp, tone.border, RoundedCornerShape(50))
                                .padding(horizontal = 6.dp, vertical = 1.dp)
                        ) {
                            Text(
                                text = tag,
                                color = tone.foreground,
                                fontSize = 10.sp,
                                maxLines = 1
                            )
                        }
                    }
                }
                // 底栏（border-t + 统计 + 操作，对齐素言 PromptCard footer）
                HorizontalDivider(color = tone.border.copy(alpha = 0.6f))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // 底栏操作：小巧按钮（32dp 高、紧凑内边距），对齐素言 footer 操作密度
                    Button(
                        onClick = {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            onOpen()
                        },
                        modifier = Modifier.height(32.dp),
                        shape = RoundedCornerShape(8.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = tone.foreground,
                            contentColor = tone.header
                        ),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 12.dp, vertical = 0.dp)
                    ) {
                        Icon(
                            imageVector = ExternalLinkIcon,
                            contentDescription = null,
                            modifier = Modifier.size(13.dp),
                            tint = tone.header
                        )
                        Spacer(Modifier.width(5.dp))
                        Text("直达", fontSize = 13.sp, maxLines = 1)
                    }
                    OutlinedButton(
                        onClick = {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            onCopyUrl()
                        },
                        modifier = Modifier.height(32.dp),
                        shape = RoundedCornerShape(8.dp),
                        border = BorderStroke(1.dp, tone.border),
                        colors = ButtonDefaults.outlinedButtonColors(
                            containerColor = appColors.Surface,
                            contentColor = tone.foreground
                        ),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 12.dp, vertical = 0.dp)
                    ) {
                        Icon(
                            imageVector = GlobeIcon,
                            contentDescription = null,
                            modifier = Modifier.size(13.dp),
                            tint = tone.foreground
                        )
                        Spacer(Modifier.width(5.dp))
                        Text("复制", fontSize = 13.sp, maxLines = 1)
                    }
                }
            }
        }
    }
}

private data class ProjectCardTone(
    val border: Color,
    val header: Color,
    val tag: Color,
    val foreground: Color
)

@Composable
private fun projectCardTone(index: Int): ProjectCardTone {
    val dark = appColors.Background == DarkAppColors.Background
    val tones = if (dark) {
        listOf(
            ProjectCardTone(Color(0xFF3D4B40), Color(0xFF263129), Color(0xFF263129), Color(0xFFC7DBC9)),
            ProjectCardTone(Color(0xFF3A4A55), Color(0xFF25313A), Color(0xFF25313A), Color(0xFFC2D7E3)),
            ProjectCardTone(Color(0xFF514047), Color(0xFF382C31), Color(0xFF382C31), Color(0xFFE5C8D0)),
            ProjectCardTone(Color(0xFF4F4737), Color(0xFF373125), Color(0xFF373125), Color(0xFFE4D4B8)),
            ProjectCardTone(Color(0xFF494356), Color(0xFF302D38), Color(0xFF302D38), Color(0xFFD7CBE5)),
            ProjectCardTone(Color(0xFF4E4039), Color(0xFF362D29), Color(0xFF362D29), Color(0xFFE3CBBF)),
            ProjectCardTone(Color(0xFF3D4B40), Color(0xFF2A3530), Color(0xFF2A3530), Color(0xFFA8BEB5)),
            ProjectCardTone(Color(0xFF4A4740), Color(0xFF34322D), Color(0xFF34322D), Color(0xFFD8D1C5))
        )
    } else {
        listOf(
            ProjectCardTone(Color(0xFFC9D5C6), Color(0xFFE7EDE4), Color(0xFFE7EDE4), Color(0xFF42594D)),
            ProjectCardTone(Color(0xFFC4D0D8), Color(0xFFE2E9EE), Color(0xFFE2E9EE), Color(0xFF435663)),
            ProjectCardTone(Color(0xFFD8C4CA), Color(0xFFEEE2E4), Color(0xFFEEE2E4), Color(0xFF664D55)),
            ProjectCardTone(Color(0xFFD8CEB8), Color(0xFFEEE8DC), Color(0xFFEEE8DC), Color(0xFF625846)),
            ProjectCardTone(Color(0xFFCEC6D8), Color(0xFFE8E4EE), Color(0xFFE8E4EE), Color(0xFF574F68)),
            ProjectCardTone(Color(0xFFD8C6BB), Color(0xFFEEE3DC), Color(0xFFEEE3DC), Color(0xFF665048)),
            ProjectCardTone(Color(0xFFC9D5C6), Color(0xFFEEF1EC), Color(0xFFEEF1EC), Color(0xFF536962)),
            ProjectCardTone(Color(0xFFCFCBC3), Color(0xFFE7E4DF), Color(0xFFE7E4DF), Color(0xFF59554D))
        )
    }
    return tones[index % tones.size]
}

private fun pixGoLineIcon(name: String, pathData: String): ImageVector =
    ImageVector.Builder(
        name = name,
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f
    ).apply {
        addPath(
            pathData = PathParser().parsePathString(pathData).toNodes(),
            pathFillType = PathFillType.NonZero,
            name = name,
            fill = null,
            stroke = SolidColor(Color.Black),
            strokeLineWidth = 1.5f,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round
        )
    }.build()
