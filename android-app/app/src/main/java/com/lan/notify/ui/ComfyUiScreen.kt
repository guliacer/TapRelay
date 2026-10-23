package com.lan.notify.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Paint as AndroidPaint
import android.os.Build
import android.os.SystemClock
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import android.widget.Toast
import java.io.ByteArrayOutputStream
import java.util.Locale
import kotlin.math.roundToInt
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.core.content.ContextCompat
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Paint as ComposePaint
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.zIndex
import com.lan.notify.R
import com.lan.notify.data.ComfyOutputFile
import com.lan.notify.data.ComfyImageCache
import com.lan.notify.data.ComfyImageSaver
import com.lan.notify.data.ComfyParam
import com.lan.notify.data.ComfyResultStore
import com.lan.notify.data.ComfyUiRepository
import com.lan.notify.data.ComfyWorkflowDetail
import com.lan.notify.data.ComfyWorkflowOrderStore
import com.lan.notify.data.ComfyWorkflowResults
import com.lan.notify.data.ComfyWorkflowSummary
import com.lan.notify.data.ServerConfig
import com.lan.notify.ui.theme.appColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val MAX_IMAGE_UPLOAD_BYTES = 64 * 1024 * 1024

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ComfyUiScreen(
    serverHost: String,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val repository = remember(serverHost) {
        ComfyUiRepository(ServerConfig(serverHost).httpBaseUrl)
    }
    var workflows by remember { mutableStateOf<List<ComfyWorkflowSummary>>(emptyList()) }
    var selected by remember { mutableStateOf<ComfyWorkflowDetail?>(null) }
    var selectedWorkflowId by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf("") }
    val values = remember { mutableStateMapOf<String, String>() }
    var uploadKey by remember { mutableStateOf<String?>(null) }
    var uploadUri by remember { mutableStateOf<android.net.Uri?>(null) }
    var promptId by remember { mutableStateOf<String?>(null) }
    var runWorkflowId by remember { mutableStateOf<String?>(null) }
    var runText by remember { mutableStateOf("") }
    var outputs by remember { mutableStateOf<List<ComfyOutputFile>>(emptyList()) }
    var runFailed by remember { mutableStateOf(false) }
    var showResults by remember { mutableStateOf(false) }
    var showGallery by remember { mutableStateOf(false) }
    var galleryGroups by remember { mutableStateOf<List<ComfyWorkflowResults>>(emptyList()) }
    var galleryLoading by remember { mutableStateOf(false) }
    var renameOpen by remember { mutableStateOf(false) }
    var renameText by remember { mutableStateOf("") }
    var renameSaving by remember { mutableStateOf(false) }
    var renameError by remember { mutableStateOf("") }
    var pendingSaveOutput by remember { mutableStateOf<ComfyOutputFile?>(null) }
    var savingOutputKey by remember { mutableStateOf<String?>(null) }
    var receivingOutputKey by remember { mutableStateOf<String?>(null) }
    var sendingSuyanKey by remember { mutableStateOf<String?>(null) }
    var runningPrompt by remember { mutableStateOf("") }
    var runElapsedSeconds by remember { mutableStateOf(0L) }
    val saveStatus = remember { mutableStateMapOf<String, String>() }
    var workflowActionText by remember { mutableStateOf("") }
    var importUri by remember { mutableStateOf<android.net.Uri?>(null) }
    var exportWorkflowId by remember { mutableStateOf<String?>(null) }
    var deletingWorkflowId by remember { mutableStateOf<String?>(null) }
    var loraFiles by remember { mutableStateOf<List<String>>(emptyList()) }
    var loraFilesLoaded by remember { mutableStateOf(false) }
    val maskBases = remember { mutableStateMapOf<String, String>() }
    var maskEditor by remember { mutableStateOf<MaskTarget?>(null) }
    val scope = rememberCoroutineScope()
    var workflowOrder by remember { mutableStateOf(ComfyWorkflowOrderStore.load(context)) }
    val orderedWorkflows = remember(workflows, workflowOrder) {
        ComfyWorkflowOrderStore.apply(workflows, workflowOrder)
    }

    /** 合并服务端最新列表并修剪本地顺序，保证已删除 id 不会无限累积。 */
    fun updateWorkflows(next: List<ComfyWorkflowSummary>) {
        workflows = next
        val normalized = ComfyWorkflowOrderStore.normalize(next, workflowOrder)
        if (normalized != workflowOrder) {
            workflowOrder = normalized
            ComfyWorkflowOrderStore.save(context, normalized)
        }
    }

    fun toggleWorkflowPin(id: String) {
        val next = ComfyWorkflowOrderStore.togglePin(workflowOrder, id)
        workflowOrder = next
        ComfyWorkflowOrderStore.save(context, next)
    }

    fun reorderWorkflows(visibleIds: List<String>) {
        val next = ComfyWorkflowOrderStore.reorder(workflowOrder, visibleIds)
        workflowOrder = next
        ComfyWorkflowOrderStore.save(context, next)
    }

    fun saveOutput(file: ComfyOutputFile) {
        val key = file.stableKey()
        if (savingOutputKey != null || receivingOutputKey != null) return
        savingOutputKey = key
        saveStatus[key] = "正在保存到 Download/ComfyUI…"
        scope.launch {
            try {
                val staged = withContext(Dispatchers.IO) { ComfyImageCache.find(context, file) }
                    ?: throw IllegalStateException("手机暂存目录中没有这张图片，请重新接收")
                val folder = withContext(Dispatchers.IO) {
                    ComfyImageSaver.save(context, file.filename, staged)
                }
                saveStatus[key] = "已保存到 $folder"
            } catch (e: Exception) {
                saveStatus[key] = e.message ?: "保存图片失败"
            } finally {
                if (savingOutputKey == key) savingOutputKey = null
            }
        }
    }

    fun sendOutputToSuyan(file: ComfyOutputFile) {
        val key = file.stableKey()
        if (sendingSuyanKey != null) return
        sendingSuyanKey = key
        // 旧结果没有记录提示词时，用当前工作流参数中的正向提示词补位。
        val prompt = file.promptText.ifBlank {
            selected?.let { resolvePromptText(it.primary + it.advanced, values) }.orEmpty()
        }
        scope.launch {
            try {
                repository.sendToSuyan(file, prompt)
                Toast.makeText(context, "已发送到素言", Toast.LENGTH_SHORT).show()
            } catch (e: Exception) {
                Toast.makeText(
                    context,
                    "发送到素言失败：${e.message ?: "未知错误"}",
                    Toast.LENGTH_LONG
                ).show()
            } finally {
                if (sendingSuyanKey == key) sendingSuyanKey = null
            }
        }
    }

    fun deleteWorkflow(workflow: ComfyWorkflowSummary) {
        if (deletingWorkflowId != null) return
        deletingWorkflowId = workflow.id
        workflowActionText = "正在删除：${workflow.name}…"
        scope.launch {
            try {
                repository.deleteWorkflow(workflow.id)
                try {
                    ComfyResultStore.remove(context, workflow.id)
                } catch (_: Exception) {
                    // The workflow is already gone; a stale local index entry is harmless.
                }
                updateWorkflows(workflows.filterNot { it.id == workflow.id })
                workflowActionText = "已删除：${workflow.name}"
            } catch (e: Exception) {
                workflowActionText = "删除失败：${e.message ?: "无法删除工作流"}"
            } finally {
                if (deletingWorkflowId == workflow.id) deletingWorkflowId = null
            }
        }
    }

    fun openRenameDialog() {
        val detail = selected ?: return
        renameText = detail.title
        renameError = ""
        renameOpen = true
    }

    fun submitRename() {
        val detail = selected ?: return
        if (renameSaving) return
        val next = renameText.trim()
        if (next.isEmpty()) {
            renameError = "名称不能为空"
            return
        }
        if (next.length > 80) {
            renameError = "名称最多 80 个字符"
            return
        }
        if (next == detail.title) {
            renameOpen = false
            return
        }
        // 详情接口返回的 id 是 URL 编码形式，列表使用解码后的 id，更新列表时要用后者。
        val workflowId = selectedWorkflowId ?: detail.id
        renameSaving = true
        renameError = ""
        scope.launch {
            try {
                repository.renameWorkflow(workflowId, next)
                selected = detail.copy(title = next)
                updateWorkflows(workflows.map { if (it.id == workflowId) it.copy(name = next) else it })
                renameOpen = false
            } catch (e: Exception) {
                renameError = e.message ?: "重命名失败"
            } finally {
                renameSaving = false
            }
        }
    }

    suspend fun receiveOutput(file: ComfyOutputFile) {
        val key = file.stableKey()
        if (receivingOutputKey != null) return
        receivingOutputKey = key
        saveStatus[key] = "正在接收到 APP 私有目录…"
        try {
            ComfyImageCache.receive(context, file) { temporaryFile ->
                repository.downloadOutputTo(file, temporaryFile)
            }
            file.beforeOutput()?.let { before ->
                try {
                    ComfyImageCache.receive(context, before) { temporaryFile ->
                        repository.downloadOutputTo(before, temporaryFile)
                    }
                } catch (_: Exception) {
                    // 对比原图是临时文件，可能已过期；对比弹窗会再尝试一次下载。
                }
            }
            val sized = withContext(Dispatchers.IO) {
                val cached = ComfyImageCache.find(context, file)
                val bounds = cached?.let { readImageBounds(it.absolutePath) }
                if (bounds != null) file.copy(width = bounds.first, height = bounds.second) else file
            }
            if (sized.width > 0 && sized.height > 0) {
                outputs = outputs.map { if (it.stableKey() == key) sized else it }
                val workflowId = runWorkflowId
                if (workflowId != null) {
                    try {
                        ComfyResultStore.save(context, workflowId, outputs)
                    } catch (_: Exception) {
                        // The image is already staged; metadata persistence is best effort.
                    }
                }
            }
            saveStatus[key] = "已接收至 APP 私有目录"
        } catch (e: Exception) {
            saveStatus[key] = "接收失败：${e.message ?: "无法接收图片"}"
        } finally {
            if (receivingOutputKey == key) receivingOutputKey = null
        }
    }

    val savePermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        val file = pendingSaveOutput
        pendingSaveOutput = null
        if (file != null) {
            if (granted) saveOutput(file)
            else saveStatus[file.stableKey()] = "未获得存储权限，无法保存到 Download/ComfyUI"
        }
    }

    val requestSaveOutput: (ComfyOutputFile) -> Unit = { file ->
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.WRITE_EXTERNAL_STORAGE) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            pendingSaveOutput = file
            savePermissionLauncher.launch(Manifest.permission.WRITE_EXTERNAL_STORAGE)
        } else {
            saveOutput(file)
        }
    }

    fun navigateBack() {
        when {
            showGallery -> showGallery = false
            showResults -> showResults = false
            selected != null -> selected = null
            else -> onBack()
        }
    }

    BackHandler(enabled = showGallery || showResults || selected != null) { navigateBack() }

    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri != null) uploadUri = uri
    }
    val workflowImportPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) importUri = uri
    }
    val workflowExportPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/json")
    ) { uri ->
        val workflowId = exportWorkflowId
        exportWorkflowId = null
        if (uri != null && workflowId != null) {
            scope.launch {
                try {
                    workflowActionText = "正在导出工作流…"
                    val bytes = repository.exportWorkflow(workflowId)
                    withContext(Dispatchers.IO) {
                        context.contentResolver.openOutputStream(uri)?.use { output ->
                            output.write(bytes)
                        } ?: throw IllegalStateException("无法写入导出文件")
                    }
                    workflowActionText = "工作流已导出"
                } catch (e: Exception) {
                    workflowActionText = "导出失败：${e.message ?: "无法导出工作流"}"
                }
            }
        }
    }

    LaunchedEffect(repository) {
        loading = true
        error = ""
        try {
            updateWorkflows(repository.listWorkflows())
        } catch (e: Exception) {
            error = e.message ?: "无法读取 ComfyUI 工作流"
        } finally {
            loading = false
        }
    }

    // 每次打开汇总页都重新读取索引，只保留本机仍可预览的图片。
    LaunchedEffect(showGallery) {
        if (!showGallery) return@LaunchedEffect
        galleryLoading = true
        galleryGroups = withContext(Dispatchers.IO) {
            try {
                ComfyResultStore.loadAll(context).mapNotNull { group ->
                    val viewable = group.outputs.filter { ComfyImageCache.find(context, it) != null }
                    if (viewable.isEmpty()) null else group.copy(outputs = viewable)
                }
            } catch (_: Exception) {
                emptyList()
            }
        }
        galleryLoading = false
    }

    LaunchedEffect(uploadUri) {
        val uri = uploadUri ?: return@LaunchedEffect
        val key = uploadKey ?: return@LaunchedEffect
        try {
            runText = "图片上传中…"
            val mimeType = context.contentResolver.getType(uri)?.substringBefore(';')
            val extension = MimeTypeMap.getSingleton()
                .getExtensionFromMimeType(mimeType)
                ?.takeIf { it.matches(Regex("[A-Za-z0-9]{1,8}")) }
                ?: "png"
            val bytes = withContext(Dispatchers.IO) {
                val input = context.contentResolver.openInputStream(uri)
                    ?: throw IllegalStateException("无法读取图片")
                input.use { stream ->
                    ByteArrayOutputStream().use { output ->
                        val buffer = ByteArray(32 * 1024)
                        var total = 0
                        while (true) {
                            val count = stream.read(buffer)
                            if (count < 0) break
                            if (total + count > MAX_IMAGE_UPLOAD_BYTES) {
                                throw IllegalStateException("图片超过 64 MB，请先压缩后再上传")
                            }
                            output.write(buffer, 0, count)
                            total += count
                        }
                        output.toByteArray()
                    }
                }
            }
            values[key] = repository.uploadImage("taprelay-${System.currentTimeMillis()}.$extension", bytes)
            maskBases.remove(key)
            runText = "图片已上传"
        } catch (e: Exception) {
            runText = e.message ?: "图片上传失败"
        } finally {
            uploadUri = null
            uploadKey = null
        }
    }

    LaunchedEffect(importUri) {
        val uri = importUri ?: return@LaunchedEffect
        try {
            workflowActionText = "正在导入工作流…"
            val text = withContext(Dispatchers.IO) {
                readWorkflowText(context, uri)
            }
            val filename = withContext(Dispatchers.IO) {
                queryDisplayName(context, uri)
            }.ifBlank { "手机导入-${System.currentTimeMillis()}.api.json" }
            repository.importWorkflow(filename, text)
            updateWorkflows(repository.listWorkflows())
            workflowActionText = "已导入：$filename"
        } catch (e: Exception) {
            workflowActionText = "导入失败：${e.message ?: "无法导入工作流"}"
        } finally {
            importUri = null
        }
    }

    // 任务运行期间每秒刷新已用时长；任务结束或切换页面时自动归零。
    LaunchedEffect(promptId) {
        if (promptId == null) {
            runElapsedSeconds = 0L
            return@LaunchedEffect
        }
        val startedAt = SystemClock.elapsedRealtime()
        while (true) {
            runElapsedSeconds = (SystemClock.elapsedRealtime() - startedAt) / 1000L
            delay(1000L)
        }
    }

    LaunchedEffect(promptId) {
        val id = promptId ?: return@LaunchedEffect
        val workflowId = runWorkflowId
        while (true) {
            try {
                val job = repository.pollJob(id)
                if (job.done) {
                    val before = job.comparisonBefore?.copy(promptId = id)
                    val receivedOutputs = job.outputs.mapIndexed { index, file ->
                        val tagged = file.copy(
                            promptId = id,
                            durationSeconds = job.durationSeconds,
                            promptText = runningPrompt
                        )
                        if (index == 0 && before != null) {
                            tagged.copy(
                                beforeFilename = before.filename,
                                beforeSubfolder = before.subfolder,
                                beforeType = before.type
                            )
                        } else {
                            tagged
                        }
                    }
                    // 保留历次生成结果，最新一次排在最前面。
                    val mergedOutputs = (receivedOutputs + outputs)
                        .distinctBy { it.stableKey() }
                        .take(ComfyResultStore.MAX_OUTPUTS)
                    outputs = mergedOutputs
                    runFailed = job.failed
                    runText = if (job.failed) "执行出错" else "生成完成"
                    showResults = !job.failed && receivedOutputs.isNotEmpty()
                    if (!job.failed) {
                        if (workflowId != null) {
                            try {
                                ComfyResultStore.save(context, workflowId, mergedOutputs)
                            } catch (_: Exception) {
                                // Showing the result must not fail because the local index is unwritable.
                            }
                        }
                        receivedOutputs.forEachIndexed { index, file ->
                            runText = "生成完成 · 正在接收第 ${index + 1}/${receivedOutputs.size} 张图片"
                            receiveOutput(file)
                        }
                        if (receivedOutputs.isNotEmpty()) {
                            val failedCount = receivedOutputs.count {
                                saveStatus[it.stableKey()]?.startsWith("接收失败") == true
                            }
                            val staged = if (failedCount == 0) {
                                "本次 ${receivedOutputs.size} 张已暂存"
                            } else {
                                "本次 ${receivedOutputs.size - failedCount}/${receivedOutputs.size} 张已暂存"
                            }
                            runText = if (mergedOutputs.size > receivedOutputs.size) {
                                "生成完成 · $staged · 列表共 ${mergedOutputs.size} 张"
                            } else {
                                "生成完成 · $staged"
                            }
                        } else {
                            runText = "生成完成 · 没有图片结果"
                        }
                    }
                    promptId = null
                    break
                }
                runText = "生成中…"
            } catch (e: Exception) {
                runText = e.message ?: "等待任务结果失败"
            }
            delay(2000)
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            painter = painterResource(R.drawable.ic_stat_comfyui),
                            contentDescription = null,
                            tint = appColors.TopBarSageContent,
                            modifier = Modifier.size(22.dp)
                        )
                        Spacer(Modifier.width(10.dp))
                        AutoScrollingWorkflowTitle(
                            text = when {
                                showGallery -> "生成图像"
                                showResults -> "生成结果"
                                else -> selected?.title ?: "ComfyUI"
                            },
                            modifier = Modifier.weight(1f).padding(end = 8.dp),
                            onDoubleTap = if (selected != null && !showResults && !showGallery) {
                                { openRenameDialog() }
                            } else {
                                null
                            }
                        )
                    }
                },
                navigationIcon = {
                    TextButton(
                        onClick = ::navigateBack,
                        colors = ButtonDefaults.textButtonColors(
                            contentColor = appColors.TopBarSageContent
                        )
                    ) {
                        Text(
                            if (showGallery || selected != null || showResults) "返回" else "关闭"
                        )
                    }
                },
                actions = {
                    if (selected == null && !showResults && !showGallery) {
                        TextButton(
                            onClick = { showGallery = true },
                            colors = ButtonDefaults.textButtonColors(
                                contentColor = appColors.TopBarSageContent
                            )
                        ) {
                            Icon(
                                painter = painterResource(R.drawable.ic_comfy_gallery),
                                contentDescription = null,
                                modifier = Modifier.size(20.dp)
                            )
                            Spacer(Modifier.width(5.dp))
                            Text("生成图")
                        }
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = appColors.TopBarSage,
                    titleContentColor = appColors.TopBarSageContent,
                    navigationIconContentColor = appColors.TopBarSageContent,
                    actionIconContentColor = appColors.TopBarSageContent
                )
            )
        }
    ) { padding ->
        if (showGallery) {
            ComfyGalleryScreen(
                padding = padding,
                groups = galleryGroups,
                loading = galleryLoading,
                workflowName = { id ->
                    workflows.firstOrNull { it.id == id }?.name
                        ?: id.substringBefore('.').removePrefix("▶")
                }
            )
        } else if (selected == null) {
            WorkflowList(
                padding = padding,
                workflows = orderedWorkflows,
                pinnedIds = workflowOrder.pinned,
                loading = loading,
                error = error,
                actionText = workflowActionText,
                deletingId = deletingWorkflowId,
                onImport = {
                    workflowImportPicker.launch(arrayOf("application/json", "text/plain"))
                },
                onExport = { id ->
                    exportWorkflowId = id
                    workflowExportPicker.launch(suggestedWorkflowFileName(id))
                },
                onDelete = { workflow -> deleteWorkflow(workflow) },
                onTogglePin = { id -> toggleWorkflowPin(id) },
                onReorder = { ids -> reorderWorkflows(ids) },
                onSelect = { id ->
                    loading = true
                    error = ""
                    outputs = emptyList()
                    runText = ""
                    runFailed = false
                    showResults = false
                    saveStatus.clear()
                    scope.launch {
                        try {
                            val detail = repository.loadWorkflow(id)
                            val params = detail.primary + detail.advanced
                            selected = detail
                            selectedWorkflowId = id
                            values.clear()
                            maskBases.clear()
                            params.forEach { values[it.key] = it.value }
                            syncInitialLoraCounts(params, values)
                            if (params.any { it.isLoraNameParam() }) {
                                loraFilesLoaded = false
                                loraFiles = try {
                                    repository.listLoraFiles()
                                } catch (e: Exception) {
                                    emptyList()
                                }
                                loraFilesLoaded = true
                            }
                            val restored = try {
                                ComfyResultStore.load(context, id)
                            } catch (_: Exception) {
                                emptyList()
                            }
                            if (restored.isNotEmpty()) {
                                val hydrated = withContext(Dispatchers.IO) {
                                    restored.map { file ->
                                        if (file.width > 0 && file.height > 0) return@map file
                                        val cached = ComfyImageCache.find(context, file)
                                        val bounds = cached?.let { readImageBounds(it.absolutePath) }
                                        if (bounds != null) file.copy(width = bounds.first, height = bounds.second) else file
                                    }
                                }
                                val missingDurations = hydrated
                                    .filter { it.durationSeconds == null && it.promptId.isNotBlank() }
                                    .map { it.promptId }
                                    .distinct()
                                val durations = withContext(Dispatchers.IO) {
                                    missingDurations.mapNotNull { promptId ->
                                        try {
                                            repository.pollJob(promptId).durationSeconds?.let { promptId to it }
                                        } catch (_: Exception) {
                                            null
                                        }
                                    }.toMap()
                                }
                                val restoredWithMeta = if (durations.isEmpty()) hydrated else hydrated.map { file ->
                                    if (file.durationSeconds != null || file.promptId.isBlank()) file
                                    else durations[file.promptId]?.let { file.copy(durationSeconds = it) } ?: file
                                }
                                outputs = restoredWithMeta
                                if (restoredWithMeta != restored) {
                                    try {
                                        ComfyResultStore.save(context, id, restoredWithMeta)
                                    } catch (_: Exception) {
                                        // Metadata is cosmetic; show the restored list even if it cannot be rewritten.
                                    }
                                }
                                runText = "历史结果 · ${restored.size} 张图片"
                                val statuses = withContext(Dispatchers.IO) {
                                    restoredWithMeta.map { file ->
                                        file.stableKey() to if (ComfyImageCache.find(context, file) != null) {
                                            "已接收至 APP 私有目录"
                                        } else {
                                            "接收失败：暂存图片已丢失，可重新接收"
                                        }
                                    }
                                }
                                statuses.forEach { (key, text) -> saveStatus[key] = text }
                            }
                        } catch (e: Exception) {
                            error = e.message ?: "无法读取工作流参数"
                        } finally {
                            loading = false
                        }
                    }
                }
            )
        } else if (showResults) {
            ComfyResultsScreen(
                padding = padding,
                workflowTitle = selected!!.title,
                outputs = outputs,
                runText = runText,
                runFailed = runFailed,
                receivingOutputKey = receivingOutputKey,
                savingOutputKey = savingOutputKey,
                sendingSuyanKey = sendingSuyanKey,
                saveStatus = saveStatus,
                repository = repository,
                onSave = requestSaveOutput,
                onReceive = { file -> scope.launch { receiveOutput(file) } },
                onSendToSuyan = { file -> sendOutputToSuyan(file) }
            )
        } else {
            WorkflowEditor(
                padding = padding,
                detail = selected!!,
                values = values,
                loraFiles = loraFiles,
                loraFilesLoaded = loraFilesLoaded,
                runText = runText,
                hasResults = outputs.isNotEmpty(),
                runFailed = runFailed,
                running = promptId != null || uploadUri != null,
                runningSeconds = runElapsedSeconds,
                repository = repository,
                maskedKeys = maskBases.keys,
                onValueChange = { key, value -> values[key] = value },
                onPickImage = { key -> uploadKey = key; imagePicker.launch("image/*") },
                onEditMask = { key ->
                    val base = maskBases[key] ?: values[key].orEmpty()
                    if (base.isNotBlank()) maskEditor = MaskTarget(key, base)
                },
                onClearImage = { key ->
                    values.remove(key)
                    maskBases.remove(key)
                    runText = ""
                },
                onShowResults = { showResults = true },
                onRun = {
                    val allParams = selected!!.primary + selected!!.advanced
                    val missingImage = allParams.firstOrNull {
                        it.kind == "image" && values[it.key].orEmpty().isBlank()
                    }
                    if (missingImage != null) {
                        runText = "请先选择图片：${missingImage.label}"
                    } else {
                        scope.launch {
                            try {
                                // 不清空历史结果：新任务完成后会合并到列表顶部。
                                runFailed = false
                                runText = "提交中…"
                                val typed = allParams.associate { param ->
                                    val value = values[param.key].orEmpty()
                                    param.key to when (param.kind) {
                                        "toggle" -> value.equals("true", ignoreCase = true)
                                        "number" -> value.toDoubleOrNull() ?: value
                                        else -> value
                                    }
                                }
                                runningPrompt = resolvePromptText(allParams, values)
                                runWorkflowId = selectedWorkflowId ?: selected!!.id
                                promptId = repository.runWorkflow(selected!!.id, typed)
                            } catch (e: Exception) {
                                runText = e.message ?: "提交失败"
                            }
                        }
                    }
                }
            )
        }
    }

    maskEditor?.let { target ->
        ComfyMaskEditorDialog(
            workflowTitle = selected?.title.orEmpty(),
            repository = repository,
            baseName = target.baseName,
            onDismiss = { maskEditor = null },
            onApplied = { composed ->
                values[target.key] = composed
                maskBases[target.key] = target.baseName
                maskEditor = null
                runText = "遮罩已应用，直接运行即可"
            }
        )
    }

    if (renameOpen) {
        ComfyRenameWorkflowDialog(
            value = renameText,
            saving = renameSaving,
            error = renameError,
            onValueChange = {
                renameText = it
                if (renameError.isNotBlank()) renameError = ""
            },
            onConfirm = { submitRename() },
            onDismiss = { if (!renameSaving) renameOpen = false }
        )
    }
}

/**
 * 顶栏工作流标题：文字超出可用宽度时先停留，再匀速展开，停住后回到起点循环。
 * 双击标题可触发重命名。
 */
@Composable
private fun AutoScrollingWorkflowTitle(
    text: String,
    modifier: Modifier = Modifier,
    onDoubleTap: (() -> Unit)? = null
) {
    var viewportWidth by remember { mutableStateOf(0) }
    var textWidth by remember { mutableStateOf(0) }
    val offset = remember { Animatable(0f) }
    val density = LocalDensity.current
    val distance = (textWidth - viewportWidth).coerceAtLeast(0)
    // Lambda 每次重组都会换实例；用稳定 key + UpdatedState，避免手势在动画期间被反复重启。
    val editable = onDoubleTap != null
    val currentOnDoubleTap = rememberUpdatedState(onDoubleTap)
    val doubleTapModifier = Modifier.pointerInput(editable) {
        if (!editable) return@pointerInput
        detectTapGestures(onDoubleTap = { currentOnDoubleTap.value?.invoke() })
    }
    Box(
        modifier = modifier
            .clipToBounds()
            .onSizeChanged { viewportWidth = it.width }
            .then(doubleTapModifier)
    ) {
        Text(
            text = text,
            maxLines = 1,
            softWrap = false,
            overflow = TextOverflow.Visible,
            modifier = Modifier
                .wrapContentWidth(align = Alignment.Start, unbounded = true)
                .onSizeChanged { textWidth = it.width }
                .graphicsLayer { translationX = -offset.value }
        )
    }
    LaunchedEffect(text, distance) {
        offset.snapTo(0f)
        if (distance <= 0) return@LaunchedEffect
        val stepPixels = with(density) { 36.dp.toPx() }
        val duration = ((distance / stepPixels) * 1000f).toInt().coerceIn(1200, 12000)
        delay(900)
        while (true) {
            offset.animateTo(
                targetValue = distance.toFloat(),
                animationSpec = tween(durationMillis = duration, easing = LinearEasing)
            )
            delay(1300)
            offset.animateTo(
                targetValue = 0f,
                animationSpec = tween(durationMillis = duration, easing = LinearEasing)
            )
            delay(900)
        }
    }
}

@Composable
private fun ComfyRenameWorkflowDialog(
    value: String,
    saving: Boolean,
    error: String,
    onValueChange: (String) -> Unit,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit
) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            modifier = Modifier.widthIn(min = 280.dp, max = 560.dp).fillMaxWidth(),
            shape = RoundedCornerShape(24.dp),
            color = appColors.Surface
        ) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(start = 20.dp, top = 20.dp, end = 20.dp, bottom = 14.dp)
            ) {
                Text(
                    "重命名工作流",
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.SemiBold,
                    color = appColors.TextPrimary
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "仅修改展示名称，工作流文件与已生成的历史结果保持不变。",
                    color = appColors.TextSecondary,
                    style = MaterialTheme.typography.bodySmall
                )
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = value,
                    onValueChange = onValueChange,
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    label = { Text("工作流名称") },
                    isError = error.isNotBlank(),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedBorderColor = appColors.PrimarySage,
                        unfocusedBorderColor = appColors.BorderSoft,
                        cursorColor = appColors.PrimarySage,
                        focusedLabelColor = appColors.PrimarySage,
                        unfocusedLabelColor = appColors.TextSecondary
                    )
                )
                if (error.isNotBlank()) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        error,
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                Spacer(Modifier.height(14.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp, Alignment.End)
                ) {
                    PlainButton(
                        text = "取消",
                        variant = PlainButtonVariant.SECONDARY,
                        enabled = !saving,
                        onClick = onDismiss
                    )
                    PlainButton(
                        text = if (saving) "保存中…" else "保存",
                        enabled = !saving,
                        onClick = onConfirm
                    )
                }
            }
        }
    }
}

@Composable
private fun WorkflowList(
    padding: PaddingValues,
    workflows: List<ComfyWorkflowSummary>,
    pinnedIds: Set<String>,
    loading: Boolean,
    error: String,
    actionText: String,
    deletingId: String?,
    onImport: () -> Unit,
    onExport: (String) -> Unit,
    onDelete: (ComfyWorkflowSummary) -> Unit,
    onTogglePin: (String) -> Unit,
    onReorder: (List<String>) -> Unit,
    onSelect: (String) -> Unit
) {
    var pendingDelete by remember { mutableStateOf<ComfyWorkflowSummary?>(null) }
    var localWorkflows by remember { mutableStateOf(workflows) }
    var draggingId by remember { mutableStateOf<String?>(null) }
    var dragOffset by remember { mutableStateOf(0f) }
    val itemHeights = remember { mutableStateMapOf<String, Int>() }
    val pinnedState = rememberUpdatedState(pinnedIds)
    val reorderState = rememberUpdatedState(onReorder)

    // 服务端列表或置顶状态变化时同步本地顺序；拖动过程中不打断当前手势。
    LaunchedEffect(workflows) {
        if (draggingId == null) localWorkflows = workflows
    }

    /** 仅在置顶状态相同的相邻项之间交换，保证置顶组永远位于最上方。 */
    fun swapWorkflows(from: Int, to: Int): Boolean {
        if (from !in localWorkflows.indices || to !in localWorkflows.indices) return false
        val current = localWorkflows
        if ((current[from].id in pinnedState.value) != (current[to].id in pinnedState.value)) {
            return false
        }
        val next = current.toMutableList()
        next.add(to, next.removeAt(from))
        localWorkflows = next
        return true
    }

    fun finishDrag() {
        val ids = localWorkflows.map { it.id }
        draggingId = null
        dragOffset = 0f
        reorderState.value(ids)
    }

    val target = pendingDelete
    if (target != null) {
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            shape = RoundedCornerShape(24.dp),
            title = { Text("删除工作流") },
            text = { Text("确定删除「${target.name}」吗？删除后需要重新导入才能使用。") },
            confirmButton = {
                TextButton(
                    onClick = {
                        pendingDelete = null
                        onDelete(target)
                    },
                    colors = ButtonDefaults.textButtonColors(contentColor = appColors.Danger)
                ) { Text("删除") }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text("取消") }
            }
        )
    }
    LazyColumn(
        modifier = Modifier.fillMaxSize().padding(padding),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        item {
            WorkflowHero(
                workflowCount = workflows.size,
                loading = loading,
                onImport = onImport
            )
        }
        if (loading) {
            item { ComfyBanner(text = "正在读取工作流…", tone = ComfyBannerTone.INFO) }
        }
        if (error.isNotBlank()) {
            item { ComfyBanner(text = error, tone = ComfyBannerTone.ERROR) }
        }
        if (actionText.isNotBlank()) {
            val failed = actionText.startsWith("导入失败") ||
                actionText.startsWith("导出失败") ||
                actionText.startsWith("删除失败")
            item {
                ComfyBanner(
                    text = actionText,
                    tone = if (failed) ComfyBannerTone.ERROR else ComfyBannerTone.SUCCESS
                )
            }
        }
        if (!loading && error.isBlank() && workflows.isEmpty()) {
            item { EmptyWorkflowCard() }
        }
        items(localWorkflows, key = { it.id }) { workflow ->
            val dragging = draggingId == workflow.id
            Box(
                modifier = Modifier
                    .then(if (dragging) Modifier else Modifier.animateItem())
                    .fillMaxWidth()
                    .zIndex(if (dragging) 1f else 0f)
                    .graphicsLayer {
                        translationY = if (dragging) dragOffset else 0f
                        shadowElevation = if (dragging) 18.dp.toPx() else 0f
                        scaleX = if (dragging) 1.01f else 1f
                        scaleY = if (dragging) 1.01f else 1f
                    }
                    .onGloballyPositioned { coordinates ->
                        itemHeights[workflow.id] = coordinates.size.height
                    }
                    .pointerInput(workflow.id) {
                        detectDragGesturesAfterLongPress(
                            onDragStart = {
                                draggingId = workflow.id
                                dragOffset = 0f
                            },
                            onDrag = { change, dragAmount ->
                                change.consume()
                                dragOffset += dragAmount.y
                                val index = localWorkflows.indexOfFirst { it.id == workflow.id }
                                if (index >= 0) {
                                    if (dragOffset > 0f) {
                                        val next = localWorkflows.getOrNull(index + 1)
                                        val height = next?.let { itemHeights[it.id] } ?: 0
                                        if (next != null && height > 0 &&
                                            dragOffset > height / 2f &&
                                            swapWorkflows(index, index + 1)
                                        ) {
                                            dragOffset -= height
                                        }
                                    } else if (dragOffset < 0f) {
                                        val previous = localWorkflows.getOrNull(index - 1)
                                        val height = previous?.let { itemHeights[it.id] } ?: 0
                                        if (previous != null && height > 0 &&
                                            dragOffset < -height / 2f &&
                                            swapWorkflows(index, index - 1)
                                        ) {
                                            dragOffset += height
                                        }
                                    }
                                }
                            },
                            onDragEnd = { finishDrag() },
                            onDragCancel = { finishDrag() }
                        )
                    }
            ) {
                WorkflowCard(
                    workflow = workflow,
                    pinned = workflow.id in pinnedIds,
                    deleting = deletingId == workflow.id,
                    deleteEnabled = deletingId == null,
                    onExport = { onExport(workflow.id) },
                    onDelete = { pendingDelete = workflow },
                    onTogglePin = { onTogglePin(workflow.id) },
                    onSelect = { onSelect(workflow.id) }
                )
            }
        }
    }
}

private enum class ComfyBannerTone { INFO, SUCCESS, ERROR }

@Composable
private fun ComfyBanner(text: String, tone: ComfyBannerTone) {
    val background = when (tone) {
        ComfyBannerTone.INFO -> appColors.PrimarySageSoft
        ComfyBannerTone.SUCCESS -> appColors.Success.copy(alpha = 0.10f)
        ComfyBannerTone.ERROR -> appColors.Danger.copy(alpha = 0.10f)
    }
    val content = when (tone) {
        ComfyBannerTone.INFO -> appColors.PrimarySageStrong
        ComfyBannerTone.SUCCESS -> appColors.Success
        ComfyBannerTone.ERROR -> appColors.Danger
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        color = background
    ) {
        Text(
            text = text,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
            style = MaterialTheme.typography.bodySmall,
            color = content
        )
    }
}

@Composable
private fun ComfyLogo(size: androidx.compose.ui.unit.Dp, corner: androidx.compose.ui.unit.Dp) {
    Image(
        painter = painterResource(R.drawable.ic_comfyui_logo),
        contentDescription = null,
        contentScale = ContentScale.Crop,
        modifier = Modifier
            .size(size)
            .clip(RoundedCornerShape(corner))
    )
}

@Composable
private fun WorkflowHero(
    workflowCount: Int,
    loading: Boolean,
    onImport: () -> Unit
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(22.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.PrimarySageSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Column(Modifier.fillMaxWidth().padding(18.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ComfyLogo(size = 52.dp, corner = 15.dp)
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        "ComfyUI",
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.TextPrimary
                    )
                    Text(
                        "手机工作流控制",
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary
                    )
                }
                if (workflowCount > 0) {
                    Surface(
                        shape = RoundedCornerShape(20.dp),
                        color = appColors.Surface
                    ) {
                        Text(
                            "$workflowCount 个工作流",
                            modifier = Modifier.padding(horizontal = 10.dp, vertical = 5.dp),
                            style = MaterialTheme.typography.labelMedium,
                            color = appColors.PrimarySageStrong
                        )
                    }
                }
            }
            Spacer(Modifier.height(14.dp))
            Text(
                "选择已配置的 API 工作流，填写参数后提交到电脑端生成。点击星标可置顶，长按卡片可拖动排序。",
                style = MaterialTheme.typography.bodyMedium,
                color = appColors.TextSecondary
            )
            Spacer(Modifier.height(14.dp))
            Button(
                onClick = onImport,
                enabled = !loading,
                modifier = Modifier.fillMaxWidth().height(46.dp),
                shape = RoundedCornerShape(14.dp),
                colors = ButtonDefaults.buttonColors(
                    containerColor = appColors.PrimarySage,
                    contentColor = appColors.OnPrimary,
                    disabledContainerColor = appColors.PrimarySageSoft,
                    disabledContentColor = appColors.TextTertiary
                )
            ) { Text("导入工作流") }
        }
    }
}

@Composable
private fun EmptyWorkflowCard() {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.Surface),
        border = BorderStroke(1.dp, appColors.BorderSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 28.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            ComfyLogo(size = 50.dp, corner = 14.dp)
            Spacer(Modifier.height(12.dp))
            Text(
                "还没有工作流",
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                color = appColors.TextPrimary
            )
            Spacer(Modifier.height(4.dp))
            Text(
                "在电脑端配置或从手机导入 .api.json 后即可开始使用。",
                style = MaterialTheme.typography.bodySmall,
                color = appColors.TextSecondary,
                textAlign = TextAlign.Center
            )
        }
    }
}

@Composable
private fun WorkflowCard(
    workflow: ComfyWorkflowSummary,
    pinned: Boolean,
    deleting: Boolean,
    deleteEnabled: Boolean,
    onExport: () -> Unit,
    onDelete: () -> Unit,
    onTogglePin: () -> Unit,
    onSelect: () -> Unit
) {
    Card(
        onClick = onSelect,
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.Surface),
        border = BorderStroke(1.dp, appColors.BorderSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Column(Modifier.fillMaxWidth().padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ComfyLogo(size = 44.dp, corner = 13.dp)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        workflow.name,
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.TextPrimary,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(Modifier.height(6.dp))
                    Surface(
                        shape = RoundedCornerShape(6.dp),
                        color = appColors.PrimarySageSoft
                    ) {
                        Text(
                            workflow.category.ifBlank { "未分类" },
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp),
                            style = MaterialTheme.typography.labelSmall,
                            color = appColors.PrimarySageStrong
                        )
                    }
                }
                Spacer(Modifier.width(4.dp))
                Box(
                    modifier = Modifier
                        .size(36.dp)
                        .clip(CircleShape)
                        .clickable(onClick = onTogglePin),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        painter = painterResource(R.drawable.ic_comfy_star),
                        contentDescription = if (pinned) "取消置顶" else "置顶工作流",
                        modifier = Modifier.size(22.dp),
                        tint = if (pinned) appColors.StarActive else appColors.TextTertiary
                    )
                }
                Text("›", color = appColors.TextTertiary, fontSize = 24.sp)
            }
            Spacer(Modifier.height(8.dp))
            HorizontalDivider(color = appColors.BorderSoft)
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    "点击卡片打开",
                    modifier = Modifier.weight(1f).padding(start = 4.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = appColors.TextTertiary
                )
                TextButton(
                    onClick = onExport,
                    colors = ButtonDefaults.textButtonColors(contentColor = appColors.PrimarySage)
                ) { Text("导出") }
                TextButton(
                    onClick = onDelete,
                    enabled = deleteEnabled,
                    colors = ButtonDefaults.textButtonColors(
                        contentColor = appColors.Danger,
                        disabledContentColor = appColors.TextTertiary
                    )
                ) { Text(if (deleting) "删除中…" else "删除") }
            }
        }
    }
}

@Composable
private fun WorkflowEditor(
    padding: PaddingValues,
    detail: ComfyWorkflowDetail,
    values: Map<String, String>,
    loraFiles: List<String>,
    loraFilesLoaded: Boolean,
    runText: String,
    hasResults: Boolean,
    runFailed: Boolean,
    running: Boolean,
    runningSeconds: Long,
    repository: ComfyUiRepository,
    maskedKeys: Set<String>,
    onValueChange: (String, String) -> Unit,
    onPickImage: (String) -> Unit,
    onEditMask: (String) -> Unit,
    onClearImage: (String) -> Unit,
    onShowResults: () -> Unit,
    onRun: () -> Unit
) {
    val loraGroups = remember(detail) { buildLoraGroups(detail.primary + detail.advanced) }
    val loraKeys = remember(loraGroups) {
        buildSet {
            loraGroups.forEach { group ->
                add(group.count.key)
                group.slots.forEach { slot ->
                    add(slot.name.key)
                    slot.strength?.let { add(it.key) }
                }
            }
        }
    }
    val loraNote = if (loraFilesLoaded && loraFiles.isEmpty()) {
        "未能读取 ComfyUI 的完整 LoRA 列表，仅显示控制台预留的 200 项"
    } else {
        ""
    }
    val basicParams = remember(detail, loraKeys) {
        detail.primary.filterNot { it.key in loraKeys }
    }
    val advancedParams = remember(detail, loraKeys) {
        detail.advanced.filterNot { it.key in loraKeys }
    }
    LazyColumn(
        modifier = Modifier.fillMaxSize().padding(padding),
        contentPadding = PaddingValues(start = 16.dp, top = 16.dp, end = 16.dp, bottom = 24.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        item {
            RunPanel(
                runText = runText,
                hasResults = hasResults,
                runFailed = runFailed,
                running = running,
                runningSeconds = runningSeconds,
                onShowResults = onShowResults,
                onRun = onRun
            )
        }
        item { ComfySectionHeader(title = "基本参数", count = basicParams.size) }
        items(basicParams, key = { it.key }) { param ->
            ParamEditor(
                param = param,
                value = values[param.key].orEmpty(),
                choices = if (param.isLoraNameParam() && loraFiles.isNotEmpty()) loraFiles else param.choices,
                choiceNote = if (param.isLoraNameParam()) loraNote else "",
                repository = repository,
                maskApplied = param.key in maskedKeys,
                onValueChange = onValueChange,
                onPickImage = onPickImage,
                onEditMask = onEditMask,
                onClearImage = onClearImage
            )
        }
        if (loraGroups.isNotEmpty()) {
            item { ComfySectionHeader(title = "LoRA 加载", count = loraGroups.size) }
            items(loraGroups, key = { "lora:${it.node}" }) { group ->
                LoraGroupEditor(
                    group = group,
                    values = values,
                    loraFiles = loraFiles,
                    loraNote = loraNote,
                    onValueChange = onValueChange
                )
            }
        }
        if (advancedParams.isNotEmpty()) {
            item { ComfySectionHeader(title = "高级参数", count = advancedParams.size) }
            items(advancedParams, key = { it.key }) { param ->
                ParamEditor(
                    param = param,
                    value = values[param.key].orEmpty(),
                    choices = if (param.isLoraNameParam() && loraFiles.isNotEmpty()) loraFiles else param.choices,
                    choiceNote = if (param.isLoraNameParam()) loraNote else "",
                    repository = repository,
                    maskApplied = param.key in maskedKeys,
                    onValueChange = onValueChange,
                    onPickImage = onPickImage,
                    onEditMask = onEditMask,
                    onClearImage = onClearImage
                )
            }
        }
    }
}

@Composable
private fun ComfySectionHeader(title: String, count: Int? = null, unit: String = "项") {
    Row(
        modifier = Modifier.fillMaxWidth().padding(top = 6.dp, bottom = 2.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .width(4.dp)
                .height(18.dp)
                .clip(RoundedCornerShape(2.dp))
                .background(appColors.PrimarySage)
        )
        Spacer(Modifier.width(8.dp))
        Text(
            title,
            style = MaterialTheme.typography.titleSmall,
            fontWeight = FontWeight.SemiBold,
            color = appColors.TextPrimary
        )
        if (count != null) {
            Spacer(Modifier.width(6.dp))
            Text(
                "$count $unit",
                style = MaterialTheme.typography.labelSmall,
                color = appColors.TextTertiary
            )
        }
    }
}

@Composable
private fun RunPanel(
    runText: String,
    hasResults: Boolean,
    runFailed: Boolean,
    running: Boolean,
    runningSeconds: Long,
    onShowResults: () -> Unit,
    onRun: () -> Unit
) {
    val pulse = if (running) {
        val transition = rememberInfiniteTransition(label = "comfyRunning")
        val value by transition.animateFloat(
            initialValue = 0.35f,
            targetValue = 1f,
            animationSpec = infiniteRepeatable(
                animation = tween(durationMillis = 900, easing = FastOutSlowInEasing),
                repeatMode = RepeatMode.Reverse
            ),
            label = "comfyRunningPulse"
        )
        value
    } else {
        1f
    }

    Card(
        modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.PrimarySageSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
        border = if (running) {
            BorderStroke(1.dp, appColors.PrimarySage.copy(alpha = 0.12f + 0.28f * pulse))
        } else {
            null
        }
    ) {
        Column(Modifier.fillMaxWidth()) {
            if (running) {
                LinearProgressIndicator(
                    modifier = Modifier.fillMaxWidth().height(3.dp),
                    color = appColors.PrimarySage,
                    trackColor = appColors.PrimarySage.copy(alpha = 0.16f)
                )
            }
            Column(Modifier.fillMaxWidth().padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        painter = painterResource(R.drawable.ic_stat_comfyui),
                        contentDescription = null,
                        tint = appColors.PrimarySageStrong,
                        modifier = Modifier.size(20.dp)
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        "运行工作流",
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.PrimarySageStrong
                    )
                    Spacer(Modifier.weight(1f))
                    if (running) {
                        Surface(
                            shape = RoundedCornerShape(50),
                            color = appColors.PrimarySage.copy(alpha = 0.14f)
                        ) {
                            Row(
                                modifier = Modifier.padding(horizontal = 10.dp, vertical = 4.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Box(
                                    Modifier
                                        .size(6.dp)
                                        .graphicsLayer {
                                            alpha = 0.4f + 0.6f * pulse
                                            scaleX = 0.8f + 0.2f * pulse
                                            scaleY = 0.8f + 0.2f * pulse
                                        }
                                        .background(appColors.PrimarySage, CircleShape)
                                )
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    "处理中",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = appColors.PrimarySageStrong
                                )
                            }
                        }
                    }
                }
                if (!running) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        runText.ifBlank { "填写参数后提交到电脑端 ComfyUI。" },
                        style = MaterialTheme.typography.bodySmall,
                        color = if (runFailed) appColors.Danger else appColors.TextSecondary
                    )
                }
                Spacer(Modifier.height(12.dp))
                Button(
                    onClick = onRun,
                    enabled = !running,
                    modifier = Modifier.fillMaxWidth().height(48.dp),
                    shape = RoundedCornerShape(14.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = appColors.PrimarySage,
                        contentColor = appColors.OnPrimary,
                        disabledContainerColor = appColors.PrimarySage.copy(alpha = 0.22f),
                        disabledContentColor = appColors.PrimarySageStrong
                    )
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (running) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(16.dp),
                                strokeWidth = 2.dp,
                                color = appColors.PrimarySageStrong
                            )
                            Spacer(Modifier.width(8.dp))
                        }
                        Text(
                            if (running) {
                                if (runningSeconds < 1L) {
                                    "正在运行…"
                                } else {
                                    "正在运行 · 已用 ${formatElapsedTime(runningSeconds)}"
                                }
                            } else {
                                "运行工作流"
                            }
                        )
                    }
                }
                if (hasResults) {
                    Spacer(Modifier.height(10.dp))
                    OutlinedButton(
                        onClick = onShowResults,
                        modifier = Modifier.fillMaxWidth().height(46.dp),
                        shape = RoundedCornerShape(14.dp),
                        border = BorderStroke(1.dp, appColors.PrimarySage),
                        colors = ButtonDefaults.outlinedButtonColors(
                            contentColor = appColors.PrimarySageStrong
                        )
                    ) { Text("查看生成结果") }
                }
            }
        }
    }
}

@Composable
private fun ComfyGalleryScreen(
    padding: PaddingValues,
    groups: List<ComfyWorkflowResults>,
    loading: Boolean,
    workflowName: (String) -> String
) {
    var preview by remember { mutableStateOf<GalleryPreview?>(null) }
    val totalCount = groups.sumOf { it.outputs.size }
    LazyColumn(
        modifier = Modifier.fillMaxSize().padding(padding),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        item {
            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(20.dp),
                colors = CardDefaults.cardColors(containerColor = appColors.PrimarySageSoft),
                elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
            ) {
                Column(Modifier.fillMaxWidth().padding(18.dp)) {
                    Text(
                        "跨工作流汇总",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.TextPrimary
                    )
                    Spacer(Modifier.height(6.dp))
                    Text(
                        if (loading) {
                            "正在汇总生成图像…"
                        } else {
                            "共 ${groups.size} 个工作流 · $totalCount 张图片，点击图片查看大图。"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.PrimarySageStrong
                    )
                }
            }
        }
        if (!loading && groups.isEmpty()) {
            item {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(16.dp),
                    colors = CardDefaults.cardColors(containerColor = appColors.Surface),
                    border = BorderStroke(1.dp, appColors.BorderSoft),
                    elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
                ) {
                    Column(Modifier.fillMaxWidth().padding(24.dp)) {
                        Text(
                            "还没有可查看的生成图像。",
                            style = MaterialTheme.typography.titleSmall,
                            color = appColors.TextPrimary
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "运行任意工作流后，已接收的图片会汇总到这里。",
                            style = MaterialTheme.typography.bodySmall,
                            color = appColors.TextSecondary
                        )
                    }
                }
            }
        }
        groups.forEach { group ->
            item(key = "gallery-header-${group.workflowId}") {
                ComfySectionHeader(
                    title = workflowName(group.workflowId),
                    count = group.outputs.size,
                    unit = "张"
                )
            }
            item(key = "gallery-images-${group.workflowId}") {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    itemsIndexed(
                        items = group.outputs,
                        key = { _, file -> file.stableKey() }
                    ) { index, file ->
                        GalleryTile(
                            file = file,
                            onClick = {
                                preview = GalleryPreview(group.outputs, index)
                            }
                        )
                    }
                }
            }
        }
    }
    preview?.let { target ->
        ComfyImagePreview(
            outputs = target.outputs,
            initialIndex = target.index,
            onDismiss = { preview = null }
        )
    }
}

private data class GalleryPreview(
    val outputs: List<ComfyOutputFile>,
    val index: Int
)

@Composable
private fun GalleryTile(file: ComfyOutputFile, onClick: () -> Unit) {
    Column(Modifier.width(124.dp)) {
        ComfyThumbnail(
            file = file,
            staged = true,
            modifier = Modifier.size(124.dp),
            onClick = onClick
        )
        Spacer(Modifier.height(5.dp))
        Text(
            file.filename,
            style = MaterialTheme.typography.labelSmall,
            color = appColors.TextSecondary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

@Composable
private fun ComfyResultsScreen(
    padding: PaddingValues,
    workflowTitle: String,
    outputs: List<ComfyOutputFile>,
    runText: String,
    runFailed: Boolean,
    receivingOutputKey: String?,
    savingOutputKey: String?,
    sendingSuyanKey: String?,
    saveStatus: Map<String, String>,
    repository: ComfyUiRepository,
    onSave: (ComfyOutputFile) -> Unit,
    onReceive: (ComfyOutputFile) -> Unit,
    onSendToSuyan: (ComfyOutputFile) -> Unit
) {
    var previewIndex by remember { mutableStateOf<Int?>(null) }
    var compareFile by remember { mutableStateOf<ComfyOutputFile?>(null) }
    LazyColumn(
        modifier = Modifier.fillMaxSize().padding(padding),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        item {
            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(20.dp),
                colors = CardDefaults.cardColors(containerColor = appColors.PrimarySageSoft),
                elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
            ) {
                Column(Modifier.fillMaxWidth().padding(18.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        ComfyLogo(size = 44.dp, corner = 13.dp)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(
                                workflowTitle,
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold,
                                color = appColors.TextPrimary,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis
                            )
                            Spacer(Modifier.height(3.dp))
                            Text(
                                runText.ifBlank { "电脑端已返回生成结果" },
                                style = MaterialTheme.typography.bodySmall,
                                color = if (runFailed) appColors.Danger else appColors.PrimarySageStrong
                            )
                        }
                    }
                }
            }
        }
        if (outputs.isEmpty()) {
            item {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(16.dp),
                    colors = CardDefaults.cardColors(containerColor = appColors.Surface),
                    border = BorderStroke(1.dp, appColors.BorderSoft),
                    elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
                ) {
                    Column(Modifier.fillMaxWidth().padding(24.dp)) {
                        Text(
                            "没有图片结果。",
                            style = MaterialTheme.typography.titleSmall,
                            color = appColors.TextPrimary
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "请返回参数页检查运行状态后重试。",
                            style = MaterialTheme.typography.bodySmall,
                            color = appColors.TextSecondary
                        )
                    }
                }
            }
        } else {
            itemsIndexed(
                items = outputs,
                key = { _, file -> file.stableKey() }
            ) { index, file ->
                ComfyOutputItem(
                    file = file,
                    receiving = receivingOutputKey == file.stableKey(),
                    saving = savingOutputKey == file.stableKey(),
                    sendingToSuyan = sendingSuyanKey == file.stableKey(),
                    status = saveStatus[file.stableKey()],
                    onSave = { onSave(file) },
                    onReceive = { onReceive(file) },
                    onSendToSuyan = { onSendToSuyan(file) },
                    onPreview = { previewIndex = index },
                    onCompare = { compareFile = file }
                )
            }
        }
    }
    previewIndex?.let { index ->
        if (outputs.isNotEmpty()) {
            ComfyImagePreview(
                outputs = outputs,
                initialIndex = index.coerceIn(0, outputs.lastIndex),
                onDismiss = { previewIndex = null }
            )
        }
    }
    compareFile?.let { file ->
        ComfyCompareDialog(
            file = file,
            repository = repository,
            onDismiss = { compareFile = null }
        )
    }
}

@Composable
private fun ParamEditor(
    param: ComfyParam,
    value: String,
    choices: List<String>,
    choiceNote: String,
    repository: ComfyUiRepository,
    maskApplied: Boolean,
    onValueChange: (String, String) -> Unit,
    onPickImage: (String) -> Unit,
    onEditMask: (String) -> Unit,
    onClearImage: (String) -> Unit
) {
    when {
        param.kind == "image" -> {
            ComfyImageParamEditor(
                param = param,
                value = value,
                repository = repository,
                maskApplied = maskApplied,
                onPickImage = onPickImage,
                onEditMask = onEditMask,
                onClearImage = onClearImage
            )
        }
        param.kind == "toggle" -> {
            val enabled = value.equals("true", ignoreCase = true)
            Surface(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(14.dp),
                color = appColors.Surface,
                border = BorderStroke(1.dp, appColors.BorderSoft)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        param.label,
                        modifier = Modifier.weight(1f),
                        style = MaterialTheme.typography.bodyMedium,
                        color = appColors.TextPrimary
                    )
                    Spacer(Modifier.width(10.dp))
                    Surface(
                        onClick = {
                            onValueChange(param.key, (!enabled).toString())
                        },
                        shape = RoundedCornerShape(20.dp),
                        color = if (enabled) {
                            appColors.PrimarySage
                        } else {
                            MaterialTheme.colorScheme.surfaceVariant
                        }
                    ) {
                        Text(
                            if (enabled) "已开启" else "已关闭",
                            modifier = Modifier.padding(horizontal = 14.dp, vertical = 6.dp),
                            style = MaterialTheme.typography.labelMedium,
                            color = if (enabled) appColors.OnPrimary else appColors.TextSecondary
                        )
                    }
                }
            }
        }
        choices.isNotEmpty() -> {
            var showChoices by remember { mutableStateOf(false) }
            Surface(
                onClick = { showChoices = true },
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(14.dp),
                color = appColors.Surface,
                border = BorderStroke(1.dp, appColors.BorderSoft)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            param.label,
                            style = MaterialTheme.typography.labelMedium,
                            color = appColors.TextSecondary
                        )
                        Spacer(Modifier.height(3.dp))
                        Text(
                            value.ifBlank { choices.first() },
                            style = MaterialTheme.typography.bodyMedium,
                            color = appColors.TextPrimary,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    Spacer(Modifier.width(10.dp))
                    Text(
                        "更改",
                        style = MaterialTheme.typography.labelMedium,
                        color = appColors.PrimarySage
                    )
                }
            }
            if (showChoices) {
                ChoicePickerDialog(
                    title = param.label,
                    value = value,
                    choices = choices,
                    note = choiceNote,
                    onPick = { choice ->
                        onValueChange(param.key, choice)
                        showChoices = false
                    },
                    onDismiss = { showChoices = false }
                )
            }
        }
        else -> {
            val isPrompt = param.multiline || param.group == "prompt" || param.group == "negative"
            if (isPrompt) {
                PromptCard(
                    label = param.label,
                    value = value,
                    onValueChange = { onValueChange(param.key, it) }
                )
            } else {
                OutlinedTextField(
                    value = value,
                    onValueChange = { onValueChange(param.key, it) },
                    label = { Text(param.label) },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    shape = RoundedCornerShape(14.dp),
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedBorderColor = appColors.PrimarySage,
                        unfocusedBorderColor = appColors.BorderSoft,
                        focusedLabelColor = appColors.PrimarySageStrong,
                        unfocusedLabelColor = appColors.TextSecondary,
                        cursorColor = appColors.PrimarySageStrong
                    )
                )
            }
        }
    }
}

@Composable
private fun ComfyImageParamEditor(
    param: ComfyParam,
    value: String,
    repository: ComfyUiRepository,
    maskApplied: Boolean,
    onPickImage: (String) -> Unit,
    onEditMask: (String) -> Unit,
    onClearImage: (String) -> Unit
) {
    val thumbnail by produceState<Bitmap?>(initialValue = null, key1 = value) {
        this.value = if (value.isBlank()) {
            null
        } else {
            withContext(Dispatchers.IO) {
                runCatching {
                    decodeThumbnailBytes(repository.downloadInput(value), 192)
                }.getOrNull()
            }
        }
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        color = appColors.Surface,
        border = BorderStroke(1.dp, appColors.BorderSoft)
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(62.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(appColors.PrimarySageSoft),
                    contentAlignment = Alignment.Center
                ) {
                    val image = thumbnail
                    when {
                        image != null -> Image(
                            bitmap = image.asImageBitmap(),
                            contentDescription = param.label,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize()
                        )
                        value.isNotBlank() -> CircularProgressIndicator(
                            modifier = Modifier.size(18.dp),
                            strokeWidth = 2.dp,
                            color = appColors.PrimarySage
                        )
                        else -> Text(
                            "无图",
                            style = MaterialTheme.typography.labelSmall,
                            color = appColors.TextTertiary
                        )
                    }
                }
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        param.label,
                        style = MaterialTheme.typography.labelLarge,
                        fontWeight = FontWeight.Medium,
                        color = appColors.TextPrimary
                    )
                    Spacer(Modifier.height(3.dp))
                    Text(
                        value.ifBlank { "尚未选择图片" }
                            .substringAfterLast('/').substringAfterLast('\\'),
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                    if (maskApplied) {
                        Spacer(Modifier.height(3.dp))
                        Text(
                            "已应用遮罩",
                            style = MaterialTheme.typography.labelSmall,
                            color = appColors.PrimarySageStrong
                        )
                    }
                }
                Spacer(Modifier.width(8.dp))
                PlainButton(
                    text = if (value.isBlank()) "选择" else "更换",
                    variant = PlainButtonVariant.SMALL,
                    onClick = { onPickImage(param.key) }
                )
            }
            if (value.isNotBlank()) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    PlainButton(
                        text = "画遮罩",
                        variant = PlainButtonVariant.SECONDARY,
                        modifier = Modifier.weight(1f),
                        onClick = { onEditMask(param.key) }
                    )
                    PlainButton(
                        text = "清除",
                        variant = PlainButtonVariant.DANGER,
                        onClick = { onClearImage(param.key) }
                    )
                }
            }
        }
    }
}

private const val MASK_EDITOR_MAX_EDGE = 2048

private enum class MaskTool { BRUSH, ERASER }

private data class MaskTarget(val key: String, val baseName: String)

private class MaskStroke(
    val points: MutableList<Offset>,
    val widthPx: Float,
    val eraser: Boolean
)

@Composable
private fun ComfyMaskEditorDialog(
    workflowTitle: String,
    repository: ComfyUiRepository,
    baseName: String,
    onDismiss: () -> Unit,
    onApplied: (String) -> Unit
) {
    val scope = rememberCoroutineScope()
    val maskColor = appColors.Danger
    var image by remember { mutableStateOf<Bitmap?>(null) }
    var loadError by remember { mutableStateOf("") }
    var applying by remember { mutableStateOf(false) }
    var applyError by remember { mutableStateOf("") }
    var tool by remember { mutableStateOf(MaskTool.BRUSH) }
    var brushSize by remember { mutableStateOf(5f) }
    val strokes = remember { mutableStateListOf<MaskStroke>() }
    var revision by remember { mutableStateOf(0) }
    var scale by remember { mutableStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }

    LaunchedEffect(baseName) {
        try {
            image = withContext(Dispatchers.IO) {
                val bytes = repository.downloadInput(baseName)
                decodeMaskEditorBitmap(bytes)
                    ?: throw IllegalStateException("无法解码原图")
            }
        } catch (e: Exception) {
            loadError = e.message ?: "读取原图失败"
        }
    }

    Dialog(
        onDismissRequest = { if (!applying) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(modifier = Modifier.fillMaxSize(), color = appColors.Background) {
            Column(
                modifier = Modifier.fillMaxSize().systemBarsPadding().padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Column(Modifier.fillMaxWidth()) {
                    Text(
                        "涂抹遮罩",
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.TextPrimary
                    )
                    Spacer(Modifier.height(3.dp))
                    Text(
                        (if (workflowTitle.isBlank()) "" else "$workflowTitle · ") +
                            "涂红的区域会被重绘；单指涂抹，双指缩放/平移",
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary
                    )
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    MaskToolButton("画笔", tool == MaskTool.BRUSH) { tool = MaskTool.BRUSH }
                    MaskToolButton("橡皮", tool == MaskTool.ERASER) { tool = MaskTool.ERASER }
                    Spacer(Modifier.weight(1f))
                    PlainButton(
                        text = "撤销",
                        variant = PlainButtonVariant.SMALL,
                        enabled = strokes.isNotEmpty() && !applying,
                        onClick = {
                            if (strokes.isNotEmpty()) {
                                strokes.removeAt(strokes.lastIndex)
                                revision++
                            }
                        }
                    )
                    PlainButton(
                        text = "清空",
                        variant = PlainButtonVariant.DANGER,
                        enabled = strokes.isNotEmpty() && !applying,
                        onClick = {
                            strokes.clear()
                            revision++
                        }
                    )
                    PlainButton(
                        text = "复位",
                        variant = PlainButtonVariant.SMALL,
                        enabled = (scale > 1.001f || offset != Offset.Zero) && !applying,
                        onClick = {
                            scale = 1f
                            offset = Offset.Zero
                        }
                    )
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        "笔刷",
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary
                    )
                    Slider(
                        value = brushSize,
                        onValueChange = { brushSize = it },
                        valueRange = 1f..25f,
                        modifier = Modifier.weight(1f).padding(horizontal = 8.dp),
                        colors = SliderDefaults.colors(
                            thumbColor = appColors.PrimarySage,
                            activeTrackColor = appColors.PrimarySage,
                            inactiveTrackColor = appColors.BorderSoft
                        ),
                        enabled = !applying
                    )
                    Text(
                        "${brushSize.roundToInt()}%",
                        modifier = Modifier.width(38.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary,
                        textAlign = TextAlign.End
                    )
                }

                BoxWithConstraints(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(Color.Black),
                    contentAlignment = Alignment.Center
                ) {
                    val source = image
                    when {
                        loadError.isNotBlank() -> Text(
                            loadError,
                            modifier = Modifier.padding(20.dp),
                            style = MaterialTheme.typography.bodyMedium,
                            color = Color.White,
                            textAlign = TextAlign.Center
                        )
                        source == null -> CircularProgressIndicator(color = Color.White)
                        else -> {
                            val ratio = source.width.toFloat() / source.height.toFloat()
                            val fittedWidth: Dp
                            val fittedHeight: Dp
                            if (maxWidth.value / maxHeight.value > ratio) {
                                fittedHeight = maxHeight
                                fittedWidth = Dp(maxHeight.value * ratio)
                            } else {
                                fittedWidth = maxWidth
                                fittedHeight = Dp(maxWidth.value / ratio)
                            }
                            MaskCanvas(
                                source = source,
                                strokes = strokes,
                                revision = revision,
                                brushSize = brushSize,
                                tool = tool,
                                scale = scale,
                                offset = offset,
                                maskColor = maskColor,
                                enabled = !applying,
                                modifier = Modifier.size(fittedWidth, fittedHeight),
                                onTransform = { newScale, newOffset ->
                                    scale = newScale
                                    offset = newOffset
                                },
                                onStrokeChanged = { revision++ }
                            )
                        }
                    }
                }

                if (applyError.isNotBlank()) {
                    Text(
                        applyError,
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.Danger
                    )
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    PlainButton(
                        text = "取消",
                        variant = PlainButtonVariant.SECONDARY,
                        modifier = Modifier.weight(1f),
                        enabled = !applying,
                        onClick = onDismiss
                    )
                    PlainButton(
                        text = if (applying) "合成中…" else "完成",
                        variant = PlainButtonVariant.PRIMARY,
                        modifier = Modifier.weight(1f),
                        enabled = !applying && image != null,
                        onClick = done@{
                            val source = image ?: return@done
                            if (strokes.none { !it.eraser }) {
                                applyError = "请先在图片上涂抹要重绘的区域"
                                return@done
                            }
                            applying = true
                            applyError = ""
                            scope.launch {
                                try {
                                    val maskBytes = withContext(Dispatchers.IO) {
                                        renderMaskPng(source, strokes)
                                    }
                                    val maskName = repository.uploadImage(
                                        "taprelay-mask-${System.currentTimeMillis()}.png",
                                        maskBytes
                                    )
                                    val composed = repository.composeMask(baseName, maskName)
                                    onApplied(composed)
                                } catch (e: Exception) {
                                    applyError = e.message ?: "遮罩应用失败"
                                    applying = false
                                }
                            }
                        }
                    )
                }
            }
        }
    }
}

@Composable
private fun MaskToolButton(label: String, selected: Boolean, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(12.dp),
        color = if (selected) appColors.PrimarySageSoft else Color.Transparent,
        border = BorderStroke(
            1.dp,
            if (selected) appColors.PrimarySage else appColors.BorderSoft
        )
    ) {
        Text(
            label,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 7.dp),
            style = MaterialTheme.typography.labelMedium,
            color = if (selected) appColors.PrimarySageStrong else appColors.TextSecondary
        )
    }
}

@Composable
private fun MaskCanvas(
    source: Bitmap,
    strokes: androidx.compose.runtime.snapshots.SnapshotStateList<MaskStroke>,
    revision: Int,
    brushSize: Float,
    tool: MaskTool,
    scale: Float,
    offset: Offset,
    maskColor: Color,
    enabled: Boolean,
    modifier: Modifier,
    onTransform: (Float, Offset) -> Unit,
    onStrokeChanged: () -> Unit
) {
    var containerSize by remember { mutableStateOf(IntSize.Zero) }
    val currentScale = rememberUpdatedState(scale)
    val currentOffset = rememberUpdatedState(offset)
    val currentSize = rememberUpdatedState(containerSize)
    val currentBrushSize = rememberUpdatedState(brushSize)
    val currentTool = rememberUpdatedState(tool)
    val currentEnabled = rememberUpdatedState(enabled)

    Box(
        modifier = modifier
            .onSizeChanged { containerSize = it }
            .graphicsLayer {
                scaleX = scale
                scaleY = scale
                translationX = offset.x
                translationY = offset.y
            }
            .pointerInput(source) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    if (!currentEnabled.value) return@awaitEachGesture
                    val size = currentSize.value
                    if (size.width <= 0 || size.height <= 0) return@awaitEachGesture

                    fun toImage(position: Offset): Offset {
                        val zoom = currentScale.value
                        val pan = currentOffset.value
                        val box = currentSize.value
                        val centerX = box.width / 2f + pan.x
                        val centerY = box.height / 2f + pan.y
                        val localX = (position.x - centerX) / zoom + box.width / 2f
                        val localY = (position.y - centerY) / zoom + box.height / 2f
                        return Offset(
                            (localX / box.width * source.width)
                                .coerceIn(0f, source.width.toFloat()),
                            (localY / box.height * source.height)
                                .coerceIn(0f, source.height.toFloat())
                        )
                    }

                    fun strokeWidth(): Float = maxOf(
                        2f,
                        currentBrushSize.value / 100f * minOf(source.width, source.height)
                    )

                    var multiTouch = false
                    var active: MaskStroke? = MaskStroke(
                        mutableListOf(toImage(down.position)),
                        strokeWidth(),
                        currentTool.value == MaskTool.ERASER
                    ).also { strokes.add(it) }
                    onStrokeChanged()
                    down.consume()

                    while (true) {
                        val event = awaitPointerEvent()
                        val pressed = event.changes.filter { it.pressed }
                        if (pressed.isEmpty()) break

                        if (pressed.size >= 2) {
                            if (!multiTouch) {
                                multiTouch = true
                                val stroke = active
                                if (stroke != null && stroke.points.size <= 1) {
                                    strokes.remove(stroke)
                                    onStrokeChanged()
                                }
                                active = null
                            }
                            val zoomChange = event.calculateZoom()
                            val panChange = event.calculatePan()
                            val centroid = event.calculateCentroid()
                            val oldScale = currentScale.value
                            val newScale = (oldScale * zoomChange).coerceIn(1f, 8f)
                            val box = currentSize.value
                            val oldOffset = currentOffset.value
                            val ratio = if (oldScale <= 0f) 1f else newScale / oldScale
                            val centerX = box.width / 2f
                            val centerY = box.height / 2f
                            val candidate = Offset(
                                (centroid.x - centerX) * (1f - ratio) + oldOffset.x * ratio + panChange.x,
                                (centroid.y - centerY) * (1f - ratio) + oldOffset.y * ratio + panChange.y
                            )
                            onTransform(newScale, clampMaskOffset(candidate, newScale, box))
                            event.changes.forEach { it.consume() }
                        } else if (!multiTouch) {
                            val change = pressed.first()
                            val stroke = active
                            if (stroke != null) {
                                val point = toImage(change.position)
                                val last = stroke.points.last()
                                if ((point - last).getDistance() >= 0.5f) {
                                    stroke.points.add(point)
                                    onStrokeChanged()
                                }
                            }
                            change.consume()
                        } else {
                            event.changes.forEach { it.consume() }
                        }
                    }
                }
            }
    ) {
        Image(
            bitmap = source.asImageBitmap(),
            contentDescription = null,
            contentScale = ContentScale.FillBounds,
            modifier = Modifier.fillMaxSize()
        )
        Canvas(Modifier.fillMaxSize()) {
            // revision 变化时触发重绘；strokes 本身是快照列表。
            if (revision >= 0) {
                val scaleX = size.width / source.width
                val scaleY = size.height / source.height
                drawIntoCanvas { canvas ->
                    canvas.saveLayer(Rect(Offset.Zero, size), ComposePaint())
                    strokes.forEach { stroke ->
                        val color = if (stroke.eraser) {
                            Color.Transparent
                        } else {
                            maskColor.copy(alpha = 0.55f)
                        }
                        val blend = if (stroke.eraser) BlendMode.Clear else BlendMode.SrcOver
                        val width = (stroke.widthPx * scaleX).coerceAtLeast(1f)
                        val points = stroke.points
                        if (points.size == 1) {
                            val point = points[0]
                            drawCircle(
                                color,
                                radius = width / 2f,
                                center = Offset(point.x * scaleX, point.y * scaleY),
                                blendMode = blend
                            )
                        } else {
                            // 整条笔画只画一次 Path，避免逐段半透明叠加出现“串珠”深色节点。
                            val path = Path()
                            val first = points[0]
                            path.moveTo(first.x * scaleX, first.y * scaleY)
                            for (index in 1 until points.size) {
                                val point = points[index]
                                path.lineTo(point.x * scaleX, point.y * scaleY)
                            }
                            drawPath(
                                path = path,
                                color = color,
                                style = Stroke(
                                    width = width,
                                    cap = StrokeCap.Round,
                                    join = StrokeJoin.Round
                                ),
                                blendMode = blend
                            )
                        }
                    }
                    canvas.restore()
                }
            }
        }
    }
}

private fun clampMaskOffset(candidate: Offset, scale: Float, size: IntSize): Offset {
    if (size.width <= 0 || size.height <= 0) return Offset.Zero
    val maxX = size.width * (scale - 1f) / 2f
    val maxY = size.height * (scale - 1f) / 2f
    return Offset(
        candidate.x.coerceIn(-maxX, maxX),
        candidate.y.coerceIn(-maxY, maxY)
    )
}

private fun decodeMaskEditorBitmap(bytes: ByteArray): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sampleSize = 1
    while (maxOf(bounds.outWidth, bounds.outHeight) / sampleSize > MASK_EDITOR_MAX_EDGE) {
        sampleSize *= 2
    }
    val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
}

private fun decodeThumbnailBytes(bytes: ByteArray, targetSize: Int): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sampleSize = 1
    while (bounds.outWidth / (sampleSize * 2) >= targetSize &&
        bounds.outHeight / (sampleSize * 2) >= targetSize
    ) {
        sampleSize *= 2
    }
    val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
}

private fun renderMaskPng(source: Bitmap, strokes: List<MaskStroke>): ByteArray {
    val mask = Bitmap.createBitmap(source.width, source.height, Bitmap.Config.ARGB_8888)
    val canvas = android.graphics.Canvas(mask)
    canvas.drawColor(android.graphics.Color.BLACK)
    val paint = AndroidPaint(AndroidPaint.ANTI_ALIAS_FLAG).apply {
        style = AndroidPaint.Style.STROKE
        strokeCap = AndroidPaint.Cap.ROUND
        strokeJoin = AndroidPaint.Join.ROUND
    }
    for (stroke in strokes) {
        paint.color = if (stroke.eraser) android.graphics.Color.BLACK else android.graphics.Color.WHITE
        paint.strokeWidth = stroke.widthPx
        val points = stroke.points
        if (points.size == 1) {
            val point = points[0]
            canvas.drawLine(point.x, point.y, point.x + 0.01f, point.y, paint)
        } else {
            for (index in 1 until points.size) {
                val from = points[index - 1]
                val to = points[index]
                canvas.drawLine(from.x, from.y, to.x, to.y, paint)
            }
        }
    }
    val output = ByteArrayOutputStream()
    mask.compress(Bitmap.CompressFormat.PNG, 100, output)
    mask.recycle()
    return output.toByteArray()
}

@Composable
private fun ChoicePickerDialog(
    title: String,
    value: String,
    choices: List<String>,
    note: String,
    onPick: (String) -> Unit,
    onDismiss: () -> Unit
) {
    val options = remember(choices, value) {
        val merged = if (value.isNotBlank() && choices.none { it == value }) {
            listOf(value) + choices
        } else {
            choices
        }
        merged.distinct()
    }
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            modifier = Modifier.widthIn(min = 280.dp, max = 560.dp).fillMaxWidth(),
            shape = RoundedCornerShape(24.dp),
            color = appColors.Surface
        ) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(start = 20.dp, top = 20.dp, end = 20.dp, bottom = 12.dp)
            ) {
                Text(
                    title,
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.SemiBold,
                    color = appColors.TextPrimary
                )
                if (note.isNotBlank()) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        note,
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                Spacer(Modifier.height(12.dp))
                val listState = rememberLazyListState(
                    initialFirstVisibleItemIndex = (options.indexOf(value) - 1).coerceAtLeast(0)
                )
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxWidth().heightIn(max = 420.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                    contentPadding = PaddingValues(bottom = 4.dp)
                ) {
                    items(options, key = { it }) { choice ->
                        val isSelected = choice == value
                        Surface(
                            onClick = { onPick(choice) },
                            modifier = Modifier.fillMaxWidth(),
                            shape = RoundedCornerShape(12.dp),
                            color = if (isSelected) appColors.PrimarySageSoft else appColors.Surface,
                            border = BorderStroke(
                                width = 1.dp,
                                color = if (isSelected) appColors.PrimarySage else appColors.BorderSoft
                            )
                        ) {
                            Row(
                                modifier = Modifier.padding(horizontal = 12.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Box(
                                    modifier = Modifier
                                        .size(20.dp)
                                        .clip(CircleShape)
                                        .background(
                                            if (isSelected) appColors.PrimarySage
                                            else Color.Transparent
                                        )
                                        .border(
                                            width = 1.5.dp,
                                            color = if (isSelected) appColors.PrimarySage
                                            else appColors.BorderSoft,
                                            shape = CircleShape
                                        ),
                                    contentAlignment = Alignment.Center
                                ) {
                                    if (isSelected) {
                                        Icon(
                                            painter = painterResource(R.drawable.ic_comfy_check),
                                            contentDescription = "已选择",
                                            modifier = Modifier.size(14.dp),
                                            tint = appColors.OnPrimary
                                        )
                                    }
                                }
                                Text(
                                    text = choice,
                                    modifier = Modifier.weight(1f).padding(start = 10.dp),
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = if (isSelected) appColors.PrimarySageStrong
                                    else appColors.TextPrimary
                                )
                            }
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))
                PlainButton(
                    text = "关闭",
                    variant = PlainButtonVariant.SECONDARY,
                    modifier = Modifier.fillMaxWidth(),
                    onClick = onDismiss
                )
            }
        }
    }
}

private data class LoraSlotParams(
    val index: Int,
    val name: ComfyParam,
    val strength: ComfyParam?
)

private data class LoraGroupParams(
    val node: String,
    val title: String,
    val count: ComfyParam,
    val slots: List<LoraSlotParams>
)

private val LoraSlotNamePattern = Regex("""^LoRA(\d+)名称$""")
private val LoraSlotStrengthPattern = Regex("""^LoRA(\d+)强度$""")

private fun ComfyParam.inputName(): String = input.ifBlank { key.substringAfter('.') }

private fun ComfyParam.nodeId(): String = node.ifBlank { key.substringBefore('.') }

private fun ComfyParam.isLoraCountParam(): Boolean = inputName() == "LoRA数量"

private fun ComfyParam.isLoraSlotNameParam(): Boolean =
    LoraSlotNamePattern.matches(inputName())

private fun ComfyParam.isLoraNameParam(): Boolean =
    inputName() == "lora_name" || isLoraSlotNameParam()

private fun buildLoraGroups(params: List<ComfyParam>): List<LoraGroupParams> =
    params.filter { it.isLoraCountParam() }.mapNotNull { count ->
        val node = count.nodeId()
        val slots = params.mapNotNull { param ->
            if (param.nodeId() != node) return@mapNotNull null
            val index = LoraSlotNamePattern.matchEntire(param.inputName())
                ?.groupValues?.get(1)?.toIntOrNull() ?: return@mapNotNull null
            val strength = params.firstOrNull {
                it.nodeId() == node &&
                    LoraSlotStrengthPattern.matchEntire(it.inputName())
                        ?.groupValues?.get(1)?.toIntOrNull() == index
            }
            LoraSlotParams(index = index, name = param, strength = strength)
        }.sortedBy { it.index }
        if (slots.isEmpty()) {
            null
        } else {
            LoraGroupParams(
                node = node,
                title = count.label.ifBlank { "LoRA 加载" },
                count = count,
                slots = slots
            )
        }
    }

/** 把运行秒数格式化为“12 秒 / 1 分 05 秒”，用于运行按钮的计时提示。 */
private fun formatElapsedTime(seconds: Long): String {
    if (seconds < 60L) return "${seconds} 秒"
    val minutes = seconds / 60L
    val remainder = seconds % 60L
    return if (remainder == 0L) "$minutes 分" else "$minutes 分 $remainder 秒"
}

/** 从当前参数里挑出生成本图时使用的正向提示词，负向提示词不参与。 */
private fun resolvePromptText(params: List<ComfyParam>, values: Map<String, String>): String {
    val negativeHints = listOf("负向", "反向", "负面", "negative")
    val candidates = params.mapNotNull { param ->
        if (param.kind == "image" || param.kind == "number" ||
            param.kind == "toggle" || param.kind == "select"
        ) {
            return@mapNotNull null
        }
        if (negativeHints.any { param.label.contains(it, ignoreCase = true) }) {
            return@mapNotNull null
        }
        val value = values[param.key].orEmpty().trim()
        if (value.isBlank()) null else param to value
    }
    val labeled = candidates.filter { it.first.label.contains("提示词") }
    return (labeled.ifEmpty { candidates }).maxByOrNull { it.second.length }?.second.orEmpty()
}

private fun syncInitialLoraCounts(params: List<ComfyParam>, values: MutableMap<String, String>) {
    for (count in params.filter { it.isLoraCountParam() }) {
        val node = count.nodeId()
        val active = params.count { param ->
            param.nodeId() == node && param.isLoraSlotNameParam() &&
                values[param.key].orEmpty().let { it.isNotBlank() && it != "None" }
        }
        val current = values[count.key]?.toDoubleOrNull()?.toInt() ?: 0
        if (active > current) values[count.key] = active.toString()
    }
}

private fun removeLoraSlot(
    group: LoraGroupParams,
    count: Int,
    index: Int,
    values: Map<String, String>,
    onValueChange: (String, String) -> Unit
) {
    if (count <= 0 || index !in 1..count) return
    val slots = group.slots.associateBy { it.index }
    for (position in index until count) {
        val from = slots[position + 1] ?: continue
        val to = slots[position] ?: continue
        onValueChange(to.name.key, values[from.name.key].orEmpty().ifBlank { "None" })
        if (to.strength != null) {
            val moved = from.strength?.let { values[it.key] }.orEmpty()
            onValueChange(to.strength.key, moved.ifBlank { "1" })
        }
    }
    slots[count]?.let { last ->
        onValueChange(last.name.key, "None")
        last.strength?.let { onValueChange(it.key, "1") }
    }
    onValueChange(group.count.key, (count - 1).toString())
}

private fun loraBaseName(name: String): String = when {
    name.isBlank() -> "选择 LoRA"
    name == "None" -> "None（不使用）"
    else -> name.substringAfterLast('\\').substringAfterLast('/')
}

@Composable
private fun LoraGroupEditor(
    group: LoraGroupParams,
    values: Map<String, String>,
    loraFiles: List<String>,
    loraNote: String,
    onValueChange: (String, String) -> Unit
) {
    val count = (values[group.count.key]?.toDoubleOrNull()?.toInt() ?: 0)
        .coerceIn(0, group.slots.size)
    var pickerSlot by remember(group) { mutableStateOf<Int?>(null) }
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.Surface),
        border = BorderStroke(1.dp, appColors.BorderSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Column(
            modifier = Modifier.padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        group.title,
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = appColors.TextPrimary
                    )
                    Text(
                        "已启用 $count / ${group.slots.size} 个",
                        color = appColors.TextSecondary,
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                Spacer(Modifier.width(8.dp))
                PlainButton(
                    text = "新增 LoRA",
                    variant = PlainButtonVariant.SMALL,
                    enabled = count < group.slots.size,
                    onClick = {
                        if (count < group.slots.size) {
                            onValueChange(group.count.key, (count + 1).toString())
                            pickerSlot = count + 1
                        }
                    }
                )
            }
            if (count == 0) {
                Text(
                    "尚未添加 LoRA，点击「新增 LoRA」开始。",
                    color = appColors.TextSecondary,
                    style = MaterialTheme.typography.bodySmall
                )
            }
            group.slots.filter { it.index <= count }.sortedBy { it.index }
                .forEachIndexed { position, slot ->
                    if (position > 0) {
                        HorizontalDivider(color = appColors.BorderSoft)
                    }
                    val name = values[slot.name.key].orEmpty()
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Surface(
                            onClick = { pickerSlot = slot.index },
                            modifier = Modifier.weight(1f),
                            shape = RoundedCornerShape(10.dp),
                            color = appColors.PrimarySageSoft
                        ) {
                            Row(
                                modifier = Modifier.padding(horizontal = 10.dp, vertical = 10.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = if (count > 1) {
                                        "LoRA ${slot.index} · ${loraBaseName(name)}"
                                    } else {
                                        loraBaseName(name)
                                    },
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = appColors.TextPrimary,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.weight(1f)
                                )
                                Spacer(Modifier.width(2.dp))
                                Text(
                                    "›",
                                    color = appColors.TextTertiary,
                                    fontSize = 20.sp
                                )
                            }
                        }
                        PlainButton(
                            text = "移除",
                            variant = PlainButtonVariant.DANGER,
                            onClick = {
                                removeLoraSlot(group, count, slot.index, values, onValueChange)
                            }
                        )
                        slot.strength?.let { strength ->
                            OutlinedTextField(
                                value = values[strength.key].orEmpty(),
                                onValueChange = { onValueChange(strength.key, it) },
                                label = { Text("强度") },
                                singleLine = true,
                                textStyle = MaterialTheme.typography.bodyMedium,
                                keyboardOptions = KeyboardOptions(
                                    keyboardType = KeyboardType.Decimal,
                                    imeAction = ImeAction.Done
                                ),
                                modifier = Modifier.width(84.dp),
                                shape = RoundedCornerShape(10.dp),
                                colors = OutlinedTextFieldDefaults.colors(
                                    focusedBorderColor = appColors.PrimarySage,
                                    unfocusedBorderColor = appColors.BorderSoft,
                                    focusedLabelColor = appColors.PrimarySageStrong,
                                    unfocusedLabelColor = appColors.TextSecondary,
                                    cursorColor = appColors.PrimarySageStrong
                                )
                            )
                        }
                    }
                }
        }
    }

    val currentPicker = pickerSlot
    val pickerTarget = currentPicker?.let { index -> group.slots.firstOrNull { it.index == index } }
    if (pickerTarget != null) {
        val options = if (loraFiles.isNotEmpty()) {
            listOf("None") + loraFiles
        } else {
            pickerTarget.name.choices
        }
        ChoicePickerDialog(
            title = "${group.title} · LoRA ${pickerTarget.index}",
            value = values[pickerTarget.name.key].orEmpty(),
            choices = options,
            note = if (loraFiles.isEmpty()) loraNote else "",
            onPick = { choice ->
                if (choice == "None") {
                    removeLoraSlot(group, count, pickerTarget.index, values, onValueChange)
                } else {
                    onValueChange(pickerTarget.name.key, choice)
                }
                pickerSlot = null
            },
            onDismiss = { pickerSlot = null }
        )
    }
}

@Composable
private fun PromptCard(
    label: String,
    value: String,
    onValueChange: (String) -> Unit
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        color = appColors.Surface,
        border = BorderStroke(1.dp, appColors.BorderSoft)
    ) {
        Column(Modifier.fillMaxWidth().padding(bottom = 10.dp)) {
            OutlinedTextField(
                value = value,
                onValueChange = onValueChange,
                label = { Text(label) },
                modifier = Modifier.fillMaxWidth(),
                minLines = 3,
                maxLines = 6,
                shape = RoundedCornerShape(14.dp),
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = Color.Transparent,
                    unfocusedBorderColor = Color.Transparent,
                    disabledBorderColor = Color.Transparent,
                    focusedLabelColor = appColors.PrimarySageStrong,
                    unfocusedLabelColor = appColors.TextSecondary,
                    cursorColor = appColors.PrimarySageStrong
                )
            )
            PromptActions(value = value, onValueChange = onValueChange)
        }
    }
}

@Composable
private fun PromptActions(
    value: String,
    onValueChange: (String) -> Unit
) {
    val context = LocalContext.current
    val clipboard = remember(context) {
        context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    }
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        PromptActionButton(
            icon = R.drawable.ic_comfy_copy,
            text = "复制",
            enabled = value.isNotBlank(),
            modifier = Modifier.weight(1f),
            onClick = {
                clipboard.setPrimaryClip(ClipData.newPlainText("提示词", value))
                Toast.makeText(context, "已复制提示词", Toast.LENGTH_SHORT).show()
            }
        )
        PromptActionButton(
            icon = R.drawable.ic_comfy_paste,
            text = "粘贴",
            modifier = Modifier.weight(1f),
            onClick = {
                val text = clipboard.primaryClip
                    ?.takeIf { it.itemCount > 0 }
                    ?.getItemAt(0)
                    ?.coerceToText(context)
                    ?.toString()
                    .orEmpty()
                if (text.isBlank()) {
                    Toast.makeText(context, "剪贴板没有可用文本", Toast.LENGTH_SHORT).show()
                } else {
                    onValueChange(text)
                }
            }
        )
        PromptActionButton(
            icon = R.drawable.ic_comfy_clear,
            text = "清空",
            enabled = value.isNotBlank(),
            modifier = Modifier.weight(1f),
            onClick = { onValueChange("") }
        )
    }
}

@Composable
private fun PromptActionButton(
    icon: Int,
    text: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    onClick: () -> Unit
) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier.height(40.dp),
        shape = RoundedCornerShape(10.dp),
        contentPadding = PaddingValues(horizontal = 8.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = appColors.PrimarySageSoft,
            contentColor = appColors.PrimarySageStrong,
            disabledContainerColor = appColors.PrimarySageSoft,
            disabledContentColor = appColors.TextTertiary
        )
    ) {
        Icon(
            painter = painterResource(icon),
            contentDescription = null,
            modifier = Modifier.size(16.dp)
        )
        Spacer(Modifier.width(6.dp))
        Text(
            text,
            style = MaterialTheme.typography.labelMedium,
            fontWeight = FontWeight.Medium,
            maxLines = 1
        )
    }
}

@Composable
private fun ComfyOutputItem(
    file: ComfyOutputFile,
    receiving: Boolean,
    saving: Boolean,
    sendingToSuyan: Boolean,
    status: String?,
    onSave: () -> Unit,
    onReceive: () -> Unit,
    onSendToSuyan: () -> Unit,
    onPreview: () -> Unit,
    onCompare: () -> Unit
) {
    val saved = status?.startsWith("已保存") == true
    val savingNow = saving || status?.startsWith("正在保存") == true
    val receivingNow = receiving || status == null || status.startsWith("正在接收")
    val needsReceive = status?.startsWith("接收失败") == true || status?.contains("请重新接收") == true
    val staged = !receivingNow && !needsReceive
    val inlineError = status?.takeIf {
        !it.startsWith("已保存") &&
            !it.startsWith("正在保存") &&
            !it.startsWith("正在接收") &&
            !it.startsWith("已接收")
    }
    val savedFolder = status
        ?.takeIf { saved }
        ?.removePrefix("已保存到 ")
        ?.takeIf { it.isNotBlank() }
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.Surface),
        border = BorderStroke(1.dp, appColors.BorderSoft),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(10.dp),
            verticalAlignment = Alignment.Top
        ) {
            ComfyThumbnail(
                file = file,
                staged = staged || needsReceive,
                modifier = Modifier.size(112.dp),
                onClick = if (staged) onPreview else null
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        file.filename,
                        modifier = Modifier.weight(1f),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.Medium,
                        color = appColors.TextPrimary,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(Modifier.width(8.dp))
                    SuyanSendIcon(
                        sending = sendingToSuyan,
                        onClick = onSendToSuyan
                    )
                }
                val meta = file.metaText()
                if (meta.isNotBlank()) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        meta,
                        style = MaterialTheme.typography.labelSmall,
                        color = appColors.TextTertiary,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Spacer(Modifier.height(10.dp))
                when {
                    savingNow -> ComfyProgressNote("正在保存到 Download…")
                    receivingNow -> ComfyProgressNote(
                        if (receiving || status?.startsWith("正在接收") == true) "正在接收…" else "等待接收…"
                    )
                    saved -> {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                painter = painterResource(R.drawable.ic_comfy_check),
                                contentDescription = null,
                                tint = appColors.Success,
                                modifier = Modifier.size(15.dp)
                            )
                            Spacer(Modifier.width(5.dp))
                            Text(
                                "已保存到 Download",
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = FontWeight.Medium,
                                color = appColors.Success
                            )
                        }
                        if (savedFolder != null) {
                            Spacer(Modifier.height(3.dp))
                            Text(
                                savedFolder,
                                style = MaterialTheme.typography.labelSmall,
                                color = appColors.TextTertiary,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                    }
                    needsReceive -> {
                        inlineError?.let {
                            Text(
                                it,
                                style = MaterialTheme.typography.labelSmall,
                                color = appColors.Danger,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis
                            )
                            Spacer(Modifier.height(8.dp))
                        }
                        ComfyOutputButton("重试", onReceive, !saving && !receiving)
                    }
                    else -> {
                        inlineError?.let {
                            Text(
                                it,
                                style = MaterialTheme.typography.labelSmall,
                                color = appColors.Danger,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis
                            )
                            Spacer(Modifier.height(8.dp))
                        }
                        if (staged) {
                            if (file.hasComparison()) {
                                ComfyCompareButton("前后对比", onCompare, !saving && !receiving)
                                Spacer(Modifier.height(8.dp))
                            }
                            ComfyOutputButton("保存到 Download", onSave, !saving && !receiving)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SuyanSendIcon(sending: Boolean, onClick: () -> Unit) {
    val shape = RoundedCornerShape(9.dp)
    Box(
        modifier = Modifier
            .size(30.dp)
            .clip(shape)
            .background(appColors.PrimarySageSoft)
            .border(1.dp, appColors.BorderSoft, shape)
            .clickable(enabled = !sending, onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        if (sending) {
            CircularProgressIndicator(
                modifier = Modifier.size(16.dp),
                strokeWidth = 2.dp,
                color = appColors.PrimarySage
            )
        } else {
            Image(
                painter = painterResource(R.drawable.ic_suyan_logo),
                contentDescription = "发送到素言",
                modifier = Modifier.size(20.dp).clip(RoundedCornerShape(5.dp)),
                contentScale = ContentScale.Fit
            )
        }
    }
}

@Composable
private fun ComfyProgressNote(text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator(
            modifier = Modifier.size(15.dp),
            strokeWidth = 2.dp,
            color = appColors.PrimarySage
        )
        Spacer(Modifier.width(7.dp))
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = appColors.TextSecondary,
            maxLines = 1
        )
    }
}

@Composable
private fun ComfyOutputButton(text: String, onClick: () -> Unit, enabled: Boolean) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth().height(40.dp),
        shape = RoundedCornerShape(12.dp),
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 0.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = appColors.PrimarySage,
            contentColor = appColors.OnPrimary,
            disabledContainerColor = appColors.PrimarySageSoft,
            disabledContentColor = appColors.TextTertiary
        )
    ) {
        Text(text, maxLines = 1)
    }
}

@Composable
private fun ComfyCompareButton(text: String, onClick: () -> Unit, enabled: Boolean) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth().height(40.dp),
        shape = RoundedCornerShape(12.dp),
        border = BorderStroke(1.dp, appColors.PrimarySage.copy(alpha = 0.45f)),
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 0.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = appColors.PrimarySageSoft,
            contentColor = appColors.PrimarySageStrong,
            disabledContainerColor = appColors.PrimarySageSoft,
            disabledContentColor = appColors.TextTertiary
        )
    ) {
        Text(text, maxLines = 1)
    }
}

@Composable
private fun ComfyThumbnail(
    file: ComfyOutputFile,
    staged: Boolean,
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null
) {
    val context = LocalContext.current
    val thumbnail by produceState<Bitmap?>(initialValue = null, file.stableKey(), staged) {
        value = if (!staged) {
            null
        } else {
            withContext(Dispatchers.IO) {
                val path = ComfyImageCache.find(context, file)?.absolutePath
                path?.let { decodeThumbnail(it, 320) }
            }
        }
    }
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(14.dp))
            .background(appColors.Background)
            .border(1.dp, appColors.BorderSoft, RoundedCornerShape(14.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
        contentAlignment = Alignment.Center
    ) {
        val bitmap = thumbnail
        if (bitmap != null) {
            Image(
                bitmap = bitmap.asImageBitmap(),
                contentDescription = file.filename,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
        } else {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(
                    painter = painterResource(R.drawable.ic_stat_comfyui),
                    contentDescription = null,
                    tint = appColors.TextTertiary,
                    modifier = Modifier.size(26.dp)
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    if (staged) "无法预览" else "接收中…",
                    style = MaterialTheme.typography.labelSmall,
                    color = appColors.TextSecondary
                )
            }
        }
    }
}

@Composable
private fun ComfyImagePreview(
    outputs: List<ComfyOutputFile>,
    initialIndex: Int,
    onDismiss: () -> Unit
) {
    if (outputs.isEmpty()) return
    val context = LocalContext.current
    val pagerState = rememberPagerState(
        initialPage = initialIndex.coerceIn(0, outputs.lastIndex),
        pageCount = { outputs.size }
    )
    val scope = rememberCoroutineScope()
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .systemBarsPadding()
        ) {
            Column(Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(start = 14.dp, end = 8.dp, top = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            if (outputs.size > 1) {
                                "生成结果 ${pagerState.currentPage + 1}/${outputs.size}"
                            } else {
                                "生成结果"
                            },
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = Color.White
                        )
                        Text(
                            outputs[pagerState.currentPage.coerceIn(0, outputs.lastIndex)].filename,
                            style = MaterialTheme.typography.labelSmall,
                            color = Color.White.copy(alpha = 0.6f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    TextButton(onClick = onDismiss) {
                        Text("关闭", color = Color.White)
                    }
                }
                HorizontalPager(
                    state = pagerState,
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    pageSpacing = 12.dp
                ) { page ->
                    val file = outputs[page]
                    val preview by produceState<Result<Bitmap?>?>(
                        initialValue = null,
                        file.stableKey()
                    ) {
                        value = withContext(Dispatchers.IO) {
                            runCatching {
                                ComfyImageCache.find(context, file)
                                    ?.absolutePath
                                    ?.let { decodePreviewBitmap(it, 2560) }
                            }
                        }
                    }
                    Box(
                        modifier = Modifier.fillMaxSize().padding(horizontal = 12.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        val result = preview
                        if (result == null) {
                            CircularProgressIndicator(color = Color.White)
                        } else {
                            val image = result.getOrNull()
                            if (image != null) {
                                Image(
                                    bitmap = image.asImageBitmap(),
                                    contentDescription = file.filename,
                                    contentScale = ContentScale.Fit,
                                    modifier = Modifier.fillMaxSize()
                                )
                            } else {
                                Text(
                                    "无法预览这张图片",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = Color.White
                                )
                            }
                        }
                    }
                }
                if (outputs.size > 1) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(44.dp),
                        horizontalArrangement = Arrangement.Center,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        outputs.indices.forEach { index ->
                            val selected = index == pagerState.currentPage
                            Box(
                                modifier = Modifier
                                    .padding(horizontal = 4.dp)
                                    .size(if (selected) 9.dp else 7.dp)
                                    .clip(CircleShape)
                                    .background(
                                        if (selected) Color.White else Color.White.copy(alpha = 0.35f)
                                    )
                                    .clickable {
                                        scope.launch { pagerState.animateScrollToPage(index) }
                                    }
                            )
                        }
                    }
                }
            }
        }
    }
}

private data class ComfyCompareImages(
    val after: Bitmap?,
    val before: Bitmap?,
    val error: String = ""
)

@Composable
private fun ComfyCompareDialog(
    file: ComfyOutputFile,
    repository: ComfyUiRepository,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val beforeFile = remember(file.stableKey()) { file.beforeOutput() }
    var reloadKey by remember(file.stableKey()) { mutableStateOf(0) }
    val state by produceState<ComfyCompareImages?>(
        initialValue = null,
        file.stableKey(),
        reloadKey
    ) {
        value = withContext(Dispatchers.IO) {
            try {
                val afterPath = ComfyImageCache.find(context, file)?.absolutePath
                if (afterPath == null) {
                    ComfyCompareImages(null, null, "生成图不在手机暂存目录，请返回结果页重新接收")
                } else {
                    var beforePath = beforeFile?.let { cached ->
                        ComfyImageCache.find(context, cached)?.absolutePath
                    }
                    if (beforePath == null && beforeFile != null) {
                        try {
                            ComfyImageCache.receive(context, beforeFile) { temporaryFile ->
                                repository.downloadOutputTo(beforeFile, temporaryFile)
                            }
                            beforePath = ComfyImageCache.find(context, beforeFile)?.absolutePath
                        } catch (_: Exception) {
                            beforePath = null
                        }
                    }
                    val afterBitmap = decodePreviewBitmap(afterPath, 2560)
                    val beforeBitmap = beforePath?.let { decodePreviewBitmap(it, 2560) }
                    ComfyCompareImages(
                        after = afterBitmap,
                        before = beforeBitmap,
                        error = when {
                            afterBitmap == null -> "无法读取生成图"
                            beforeFile == null -> ""
                            beforeBitmap == null -> "对比原图已失效，请返回重新生成一次"
                            else -> ""
                        }
                    )
                }
            } catch (e: Exception) {
                ComfyCompareImages(null, null, e.message ?: "无法读取对比图片")
            }
        }
    }
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .systemBarsPadding()
        ) {
            Column(Modifier.fillMaxSize().padding(horizontal = 14.dp, vertical = 10.dp)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            "前后对比",
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = Color.White
                        )
                        Text(
                            file.filename,
                            style = MaterialTheme.typography.labelSmall,
                            color = Color.White.copy(alpha = 0.6f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    TextButton(onClick = onDismiss) {
                        Text("关闭", color = Color.White)
                    }
                }
                Spacer(Modifier.height(8.dp))
                Box(
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    contentAlignment = Alignment.Center
                ) {
                    val result = state
                    when {
                        result == null -> CircularProgressIndicator(color = Color.White)
                        result.error.isNotBlank() || result.after == null || result.before == null -> {
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Text(
                                    result.error.ifBlank { "对比图片尚未就绪" },
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = Color.White,
                                    textAlign = TextAlign.Center,
                                    modifier = Modifier.padding(horizontal = 20.dp)
                                )
                                Spacer(Modifier.height(14.dp))
                                Button(
                                    onClick = { reloadKey++ },
                                    shape = RoundedCornerShape(12.dp),
                                    colors = ButtonDefaults.buttonColors(
                                        containerColor = appColors.PrimarySage,
                                        contentColor = appColors.OnPrimary
                                    )
                                ) {
                                    Text("重试")
                                }
                            }
                        }
                        else -> ComfyCompareView(
                            before = result.before,
                            after = result.after,
                            modifier = Modifier.fillMaxSize()
                        )
                    }
                }
                if (state?.let { it.error.isBlank() && it.after != null && it.before != null } == true) {
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "左右拖动分割线，查看原图与生成结果",
                        style = MaterialTheme.typography.labelSmall,
                        color = Color.White.copy(alpha = 0.65f),
                        modifier = Modifier.align(Alignment.CenterHorizontally)
                    )
                }
            }
        }
    }
}

@Composable
private fun ComfyCompareView(
    before: Bitmap,
    after: Bitmap,
    modifier: Modifier = Modifier
) {
    val beforeImage = remember(before) { before.asImageBitmap() }
    val afterImage = remember(after) { after.asImageBitmap() }
    var fraction by remember { mutableStateOf(0.5f) }
    BoxWithConstraints(modifier = modifier) {
        val aspect = (after.width.toFloat() / after.height.toFloat().coerceAtLeast(1f))
            .coerceAtLeast(0.01f)
        val heightIfFullWidth = maxWidth / aspect
        val frameWidth: Dp
        val frameHeight: Dp
        if (heightIfFullWidth <= maxHeight) {
            frameWidth = maxWidth
            frameHeight = heightIfFullWidth
        } else {
            frameHeight = maxHeight
            frameWidth = maxHeight * aspect
        }
        Box(
            modifier = Modifier
                .align(Alignment.Center)
                .width(frameWidth)
                .height(frameHeight)
                .clip(RoundedCornerShape(12.dp))
                .background(Color.Black)
                .pointerInput(Unit) {
                    awaitEachGesture {
                        val down = awaitFirstDown()
                        fun update(x: Float) {
                            fraction = (x / size.width.coerceAtLeast(1)).coerceIn(0f, 1f)
                        }
                        update(down.position.x)
                        down.consume()
                        while (true) {
                            val event = awaitPointerEvent()
                            event.changes.firstOrNull()?.let { change ->
                                update(change.position.x)
                                change.consume()
                            }
                            if (event.changes.none { it.pressed }) break
                        }
                    }
                }
        ) {
            Image(
                bitmap = afterImage,
                contentDescription = "生成结果",
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
            Image(
                bitmap = beforeImage,
                contentDescription = "原图",
                contentScale = ContentScale.Crop,
                modifier = Modifier
                    .fillMaxSize()
                    .drawWithContent {
                        clipRect(right = size.width * fraction) {
                            this@drawWithContent.drawContent()
                        }
                    }
            )
            Canvas(Modifier.fillMaxSize()) {
                val x = size.width * fraction
                val centerY = size.height / 2f
                val radius = 14.dp.toPx()
                drawLine(
                    color = Color.White,
                    start = Offset(x, 0f),
                    end = Offset(x, size.height),
                    strokeWidth = 3.dp.toPx()
                )
                drawCircle(Color.White, radius = radius, center = Offset(x, centerY))
                val arrow = 4.dp.toPx()
                val leftArrow = Path().apply {
                    moveTo(x - arrow, centerY)
                    lineTo(x - arrow * 2.2f, centerY - arrow)
                    lineTo(x - arrow * 2.2f, centerY + arrow)
                    close()
                }
                drawPath(leftArrow, Color.Black)
                val rightArrow = Path().apply {
                    moveTo(x + arrow, centerY)
                    lineTo(x + arrow * 2.2f, centerY - arrow)
                    lineTo(x + arrow * 2.2f, centerY + arrow)
                    close()
                }
                drawPath(rightArrow, Color.Black)
            }
            Surface(
                modifier = Modifier.align(Alignment.TopStart).padding(10.dp),
                shape = RoundedCornerShape(8.dp),
                color = Color.Black.copy(alpha = 0.55f)
            ) {
                Text(
                    "原图",
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White
                )
            }
            Surface(
                modifier = Modifier.align(Alignment.TopEnd).padding(10.dp),
                shape = RoundedCornerShape(8.dp),
                color = Color.Black.copy(alpha = 0.55f)
            ) {
                Text(
                    "生成",
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White
                )
            }
        }
    }
}

private fun decodePreviewBitmap(path: String, maxEdge: Int): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(path, bounds)
    var sampleSize = 1
    while (maxOf(bounds.outWidth, bounds.outHeight) / (sampleSize * 2) >= maxEdge) {
        sampleSize *= 2
    }
    val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
    return BitmapFactory.decodeFile(path, options)
}

private fun ComfyOutputFile.metaText(): String {
    val parts = mutableListOf<String>()
    durationSeconds?.let { parts += String.format(Locale.US, "%.2fs", it) }
    val extension = filename.substringAfterLast('.', "").uppercase(Locale.US)
    if (extension.isNotBlank() && extension.length <= 5) parts += extension
    if (width > 0 && height > 0) parts += "${width}×${height}"
    return parts.joinToString("  ")
}

private fun readImageBounds(path: String): Pair<Int, Int>? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(path, bounds)
    return if (bounds.outWidth > 0 && bounds.outHeight > 0) {
        bounds.outWidth to bounds.outHeight
    } else {
        null
    }
}

private fun decodeThumbnail(path: String, targetSize: Int): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(path, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sampleSize = 1
    while (bounds.outWidth / (sampleSize * 2) >= targetSize &&
        bounds.outHeight / (sampleSize * 2) >= targetSize
    ) {
        sampleSize *= 2
    }
    val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
    return BitmapFactory.decodeFile(path, options)
}

private const val MAX_WORKFLOW_IMPORT_BYTES = 32 * 1024 * 1024

private fun readWorkflowText(context: Context, uri: android.net.Uri): String {
    val input = context.contentResolver.openInputStream(uri)
        ?: throw IllegalStateException("无法读取工作流文件")
    input.use { stream ->
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(32 * 1024)
        var total = 0
        while (true) {
            val count = stream.read(buffer)
            if (count < 0) break
            total += count
            if (total > MAX_WORKFLOW_IMPORT_BYTES) {
                throw IllegalStateException("工作流文件超过 32 MB")
            }
            output.write(buffer, 0, count)
        }
        return output.toString(Charsets.UTF_8.name())
    }
}

private fun queryDisplayName(context: Context, uri: android.net.Uri): String {
    context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) {
            val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (index >= 0) return cursor.getString(index).orEmpty()
        }
    }
    return uri.lastPathSegment?.substringAfterLast('/')?.substringAfterLast('\\').orEmpty()
}

private fun suggestedWorkflowFileName(id: String): String {
    val name = id.substringAfterLast('/').substringAfterLast('\\')
    return if (name.endsWith(".api.json", ignoreCase = true)) name else "$name.api.json"
}

