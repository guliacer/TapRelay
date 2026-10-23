package com.lan.notify.data

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.security.MessageDigest

/** Keeps received ComfyUI results in one app-private folder that media apps do not scan. */
object ComfyImageCache {
    private const val RESULT_DIRECTORY = "ComfyUI/received"

    fun find(context: Context, output: ComfyOutputFile): File? {
        val target = targetFile(context, output)
        return target.takeIf { it.isFile && it.length() > 0L }
    }

    suspend fun receive(
        context: Context,
        output: ComfyOutputFile,
        download: suspend (File) -> Unit
    ): File {
        val (target, partial) = withContext(Dispatchers.IO) {
            val directory = File(context.filesDir, RESULT_DIRECTORY)
            if (!directory.exists() && !directory.mkdirs()) {
                throw IOException("无法创建 ComfyUI 图片暂存目录")
            }
            val destination = targetFile(context, output)
            val temporary = File(directory, "${identity(output)}.part")
            temporary.delete()
            destination to temporary
        }
        if (target.isFile && target.length() > 0L) return target

        try {
            download(partial)
            return withContext(Dispatchers.IO) {
                if (!partial.isFile || partial.length() == 0L) {
                    throw IOException("结果图片内容为空")
                }
                if (target.exists() && !target.delete()) {
                    throw IOException("无法更新暂存图片")
                }
                if (!partial.renameTo(target)) {
                    partial.copyTo(target, overwrite = true)
                    if (!partial.delete()) partial.deleteOnExit()
                }
                target
            }
        } catch (error: Exception) {
            withContext(Dispatchers.IO) { partial.delete() }
            throw error
        }
    }

    private fun targetFile(context: Context, output: ComfyOutputFile): File {
        val extension = output.filename.substringAfterLast('.', "png")
            .takeIf { it.matches(Regex("[A-Za-z0-9]{1,10}")) }
            ?.lowercase()
            ?: "png"
        return File(File(context.filesDir, RESULT_DIRECTORY), "${identity(output)}.$extension")
    }

    private fun identity(output: ComfyOutputFile): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(output.stableKey().toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) }
    }
}
