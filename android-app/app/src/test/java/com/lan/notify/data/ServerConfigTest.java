package com.lan.notify.data;

import org.junit.Test;

import static org.junit.Assert.assertEquals;

public class ServerConfigTest {
    @Test
    public void webSocketUrlAlwaysUsesPort1122() {
        ServerConfig config = new ServerConfig("192.168.1.100");
        assertEquals("ws://192.168.1.100:1122", config.getWebSocketUrl());
    }
}
