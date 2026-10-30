package com.lan.notify.data

import android.content.Context

class UpdatePromptStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE
    )

    fun shouldPrompt(version: String): Boolean =
        !preferences.getBoolean(KEY_NEVER_PROMPT, false) &&
            preferences.getString(KEY_SKIPPED_VERSION, "") != version

    fun skipVersion(version: String) {
        preferences.edit().putString(KEY_SKIPPED_VERSION, version).apply()
    }

    fun neverPrompt() {
        preferences.edit().putBoolean(KEY_NEVER_PROMPT, true).apply()
    }

    private companion object {
        const val PREFERENCES_NAME = "notify_update_prompt"
        const val KEY_SKIPPED_VERSION = "skipped_version"
        const val KEY_NEVER_PROMPT = "never_prompt"
    }
}
