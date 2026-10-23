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
import androidx.core.content.ContextCompat
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.Locale

/** 将 PC 中转下载的 ComfyUI 图片写入公共 Download/ComfyUI 目录。 */
object ComfyImageSaver {
    const val SAVE_PATH = "Download/ComfyUI"

    @Suppress("DEPRECATION")
    fun save(context: Context, sourceName: String, sourceFile: File): String {
        require(sourceFile.isFile && sourceFile.length() > 0L) { "图片内容为空" }
        val name = createDisplayName(sourceName)
        val mimeType = mimeTypeFor(name)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
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

    @Suppress("DEPRECATION")
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
        val basename = sourceName.substringAfterLast('/').substringAfterLast('\\')
        val safeName = basename.replace(Regex("[\\u0000-\\u001F<>:\"/\\\\|?*]"), "_")
            .ifBlank { "comfyui.png" }
        return "${System.currentTimeMillis()}_$safeName"
    }

    private fun mimeTypeFor(filename: String): String {
        val extension = filename.substringAfterLast('.', "").lowercase(Locale.ROOT)
        val mimeType = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
        return mimeType?.takeIf { it.startsWith("image/") } ?: "image/png"
    }
}
