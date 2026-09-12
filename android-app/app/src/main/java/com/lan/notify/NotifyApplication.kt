package com.lan.notify

import android.app.Application
import com.lan.notify.data.MessageRepository
import com.lan.notify.notification.NotificationHelper

class NotifyApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        MessageRepository.initialize(this)
        NotificationHelper.createChannels(this)
    }
}
