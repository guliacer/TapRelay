package com.lan.notify.data

import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import androidx.annotation.RequiresApi
import androidx.core.content.ContextCompat
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.Locale

/** 将 PC 中转下载的 ComfyUI 图片写入公共 Download/ComfyUI 目录。 */
object ComfyImageSaver {
    const val SAVE_PATH = "Download/ComfyUI"

    @Suppress("DEPRECATION")
    fun save(
        context: Context,
        sourceName: String,
        sourceFile: File,
        replaceExisting: Boolean = false
    ): String {
        require(sourceFile.isFile && sourceFile.length() > 0L) { "图片内容为空" }
        val name = createDisplayName(sourceName)
        val mimeType = mimeTypeFor(name)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            if (replaceExisting) deleteExisting(context, sourceName)
            return saveWithMediaStore(context, name, mimeType, sourceFile)
        }
        if (ContextCompat.checkSelfPermission(context, android.Manifest.permission.WRITE_EXTERNAL_STORAGE) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            throw SecurityException("需要存储权限才能保存到 Download/ComfyUI")
        }

        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        val directory = File(downloads, "ComfyUI")
        if (!directory.exists() && !directory.mkdirs()) {
            throw IOException("无法创建 Download/ComfyUI 目录")
        }
        if (replaceExisting) {
            directory.listFiles { f -> f.name.endsWith("_" + safeBasename(sourceName)) }
                ?.forEach { f -> f.delete() }
        }
        val destination = File(directory, name)
        sourceFile.inputStream().buffered().use { input ->
            FileOutputStream(destination).buffered().use { output -> input.copyTo(output, 64 * 1024) }
        }
        MediaScannerConnection.scanFile(
            context,
            arrayOf(destination.absolutePath),
            arrayOf(mimeType),
            null
        )
        return SAVE_PATH
    }

    /** 该源文件是否已经保存到 Download/ComfyUI（按文件名比对，忽略时间戳前缀）。 */
    fun isSaved(context: Context, sourceName: String): Boolean {
        val basename = safeBasename(sourceName)
        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        val directory = File(downloads, "ComfyUI")
        return directory.isDirectory &&
            directory.listFiles { f -> f.name.endsWith("_$basename") }?.isNotEmpty() == true
    }

    @Suppress("DEPRECATION")
    private fun deleteExisting(context: Context, sourceName: String) {
        val basename = safeBasename(sourceName)
        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        val directory = File(downloads, "ComfyUI")
        directory.listFiles { f -> f.name.endsWith("_$basename") }?.forEach { f -> f.delete() }
    }

    @Suppress("DEPRECATION")
    @RequiresApi(Build.VERSION_CODES.Q)
    private fun saveWithMediaStore(context: Context, name: String, mimeType: String, sourceFile: File): String {
        val resolver = context.contentResolver
        // Android 10+ 的 Images 集合不允许以 Download 为顶层目录，必须写入 Downloads 集合。
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
            put(MediaStore.MediaColumns.RELATIVE_PATH, SAVE_PATH)
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri: Uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("无法创建 Download/ComfyUI 文件项")
        try {
            val output = resolver.openOutputStream(uri, "w")
                ?: throw IOException("无法打开 Download/ComfyUI 文件项")
            output.use { stream ->
                sourceFile.inputStream().buffered().use { input -> input.copyTo(stream, 64 * 1024) }
            }
            val published = ContentValues().apply {
                put(MediaStore.MediaColumns.IS_PENDING, 0)
            }
            if (resolver.update(uri, published, null, null) == 0) {
                throw IOException("无法完成 Download/ComfyUI 图片保存")
            }
        } catch (error: Exception) {
            resolver.delete(uri, null, null)
            throw error
        }
        val saved = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            "ComfyUI/$name"
        )
        MediaScannerConnection.scanFile(context, arrayOf(saved.absolutePath), arrayOf(mimeType), null)
        return SAVE_PATH
    }

    private fun createDisplayName(sourceName: String): String {
        val basename = safeBasename(sourceName)
        return "${System.currentTimeMillis()}_$basename"
    }

    private fun safeBasename(sourceName: String): String {
        val basename = sourceName.substringAfterLast('/').substringAfterLast('\\')
        return basename.replace(Regex("[\\u0000-\\u001F<>:\"/\\\\|?*]"), "_")
            .ifBlank { "comfyui.png" }
    }

    private fun mimeTypeFor(filename: String): String {
        val extension = filename.substringAfterLast('.', "").lowercase(Locale.ROOT)
        val mimeType = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
        return mimeType?.takeIf { it.startsWith("image/") } ?: "image/png"
    }
}
