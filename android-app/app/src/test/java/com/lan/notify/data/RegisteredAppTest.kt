package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Test

class RegisteredAppTest {
    @Test
    fun registeredAppMetadataRoundTripsThroughLocalStore() {
        val original = listOf(
            RegisteredApp(
                appKey = "Example-Agent",
                displayName = "Example Agent",
                brandColor = "#123456",
                iconBase64 = "iVBORw0KGgo="
            )
        )

        val loaded = LocalStore.decodeRegisteredApps(LocalStore.encodeRegisteredApps(original))

        assertEquals(original.single().copy(appKey = "example-agent"), loaded["example-agent"])
    }

    @Test
    fun serverMetadataCanOverrideBuiltInAppPresentation() {
        val loaded = LocalStore.decodeRegisteredApps(
            """[
                {"appKey":"workbuddy","displayName":"WorkBuddy Remote","brandColor":"#6351F1","iconBase64":"remote-icon"},
                {"appKey":"Example-Agent","displayName":"Example Agent","brandColor":"#123456","iconBase64":""}
            ]""".trimIndent()
        )

        assertEquals("WorkBuddy Remote", loaded["workbuddy"]?.displayName)
        assertEquals("Example Agent", loaded["example-agent"]?.displayName)
    }
}
