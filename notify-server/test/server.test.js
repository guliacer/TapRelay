import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import WebSocket from "ws";
import { startNotifyServer } from "../server.js";
import { PORT } from "../src/config.js";
import { loadApps, registerApp, saveApps } from "../src/apps.js";

let server;

beforeEach(async () => {
  server = await startNotifyServer({ host: "127.0.0.1", port: 0, logger: silentLogger(), appRegistryPath: null });
});

afterEach(async () => {
  await server?.close();
  server = undefined;
});

describe("notify-server", () => {
  it("registers an app and serves its metadata and PNG icon", async () => {
    const iconBase64 = readFileSync(
      new URL("../../pc-app/Assets/ic_chatgpt_logo.png", import.meta.url),
    ).toString("base64");

    const registerResponse = await fetch(`http://127.0.0.1:${server.port}/apps/register`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        appKey: "example-agent",
        displayName: "Example Agent",
        brandColor: "#123456",
        iconBase64,
      }),
    });

    expect(registerResponse.status).toBe(200);
    expect((await registerResponse.json()).app).toMatchObject({
      appKey: "example-agent",
      displayName: "Example Agent",
      brandColor: "#123456",
      iconPath: "/apps/icon/example-agent",
    });

    const appsResponse = await fetch(`http://127.0.0.1:${server.port}/apps`);
    expect((await appsResponse.json()).apps).toHaveLength(1);

    const iconResponse = await fetch(`http://127.0.0.1:${server.port}/apps/icon/example-agent`);
    expect(iconResponse.status).toBe(200);
    expect(iconResponse.headers.get("content-type")).toContain("image/png");
    expect((await iconResponse.arrayBuffer()).byteLength).toBeGreaterThan(0);
  });

  it("persists registered apps without leaving a temporary registry file", () => {
    const iconBase64 = readFileSync(
      new URL("../../pc-app/Assets/ic_chatgpt_logo.png", import.meta.url),
    ).toString("base64");
    const directory = mkdtempSync(path.join(os.tmpdir(), "taprelay-apps-"));
    const registryPath = path.join(directory, "notification-app-registry.json");

    try {
      const apps = new Map();
      registerApp(apps, {
        appKey: "persisted-agent",
        displayName: "Persisted Agent",
        brandColor: "#654321",
        iconBase64,
      });
      saveApps(registryPath, apps);

      expect(existsSync(registryPath)).toBe(true);
      expect(existsSync(`${registryPath}.tmp`)).toBe(false);

      const loaded = loadApps(registryPath);
      expect(loaded.get("persisted-agent")).toMatchObject({
        appKey: "persisted-agent",
        displayName: "Persisted Agent",
        brandColor: "#654321",
      });
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  it("responds to health checks", async () => {
    const response = await fetch(`http://127.0.0.1:${server.port}/health`);
    expect(response.status).toBe(200);

    const body = await response.json();
    expect(body).toMatchObject({ status: "ok", clients: 0 });
    expect(body.port).toBe(server.port);
  });

  it("rejects invalid payloads", async () => {
    const response = await fetch(`http://127.0.0.1:${server.port}/send`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message: "   " }),
    });

    expect(response.status).toBe(400);
  });

  it("rejects oversized request bodies before broadcasting", async () => {
    const response = await fetch(`http://127.0.0.1:${server.port}/send`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message: "x".repeat(20_000) }),
    });

    expect(response.status).toBe(400);
  });

  it("broadcasts websocket messages", async () => {
    const socket = new WebSocket(`ws://127.0.0.1:${server.port}`);
    await waitForOpen(socket);

    const messagePromise = waitForMessage(socket);
    const response = await fetch(`http://127.0.0.1:${server.port}/send`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        message: " Agent 任务已完成 ",
        source: "codex",
        cwd: "W:\\TapRelay",
        durationMs: 125900,
        tokenCount: 42850,
        credits: "12.5",
      }),
    });

    expect(response.status).toBe(200);
    const payload = JSON.parse(await messagePromise);
    expect(payload.type).toBe("message");
    expect(payload.content).toBe("[codex] Agent 任务已完成");
    expect(payload.cwd).toBe("W:\\TapRelay");
    expect(payload.durationMs).toBe(125900);
    expect(payload.tokenCount).toBe(42850);
    expect(payload.credits).toBe("12.5");
    expect(payload.time).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/);

    socket.close();
  });

  it("preserves a real task log separately from the status message", async () => {
    const socket = new WebSocket(`ws://127.0.0.1:${server.port}`);
    await waitForOpen(socket);

    const messagePromise = waitForMessage(socket);
    const response = await fetch(`http://127.0.0.1:${server.port}/send`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        message: "已完成鉴权模块重构并通过 12 项测试",
        taskLog: "已完成鉴权模块重构并通过 12 项测试",
        source: "example-agent",
        status: "completed",
        taskId: "task-log-1",
      }),
    });

    expect(response.status).toBe(200);
    const payload = JSON.parse(await messagePromise);
    expect(payload).toMatchObject({
      content: "[example-agent] 已完成鉴权模块重构并通过 12 项测试",
      taskLog: "已完成鉴权模块重构并通过 12 项测试",
      source: "example-agent",
      status: "completed",
      taskId: "task-log-1",
    });

    socket.close();
  });

  it("responds to business heartbeat messages", async () => {
    const socket = new WebSocket(`ws://127.0.0.1:${server.port}`);
    await waitForOpen(socket);

    const ackPromise = waitForMessage(socket);
    socket.send(JSON.stringify({ type: "heartbeat", time: 1710000000 }));

    const payload = JSON.parse(await ackPromise);
    expect(payload).toMatchObject({ type: "heartbeat_ack" });

    socket.close();
  });

  it("broadcasts live events with normalized approval defaults", async () => {
    const socket = new WebSocket(`ws://127.0.0.1:${server.port}`);
    await waitForOpen(socket);

    const eventPromise = waitForMessage(socket);
    const responsePromise = fetch(`http://127.0.0.1:${server.port}/events`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        eventType: "approval.requested",
        source: "codex",
        toolName: "Bash",
        inputSummary: "execute restricted command",
        eventId: "approval-event-1",
        durationMs: 1_250,
        tokenCount: 240,
        credits: "0.75",
        taskLog: "等待用户确认 Bash 操作",
      }),
    });

    const event = JSON.parse(await eventPromise);
    expect(event).toMatchObject({
      type: "event",
      eventId: "approval-event-1",
      eventType: "approval.requested",
      status: "waiting",
      requestId: "approval-event-1",
      requiresAction: true,
      toolName: "Bash",
      durationMs: 1250,
      tokenCount: 240,
      credits: "0.75",
      taskLog: "等待用户确认 Bash 操作",
    });
    expect(event.expiresAt).toBeGreaterThan(Date.now());

    const controlPromise = waitForPayload(socket, (payload) => payload.type === "control_result");
    const resolutionPromise = waitForPayload(socket, (payload) => payload.eventType === "approval.resolved");
    socket.send(JSON.stringify({
      type: "control",
      action: "approval.resolve",
      requestId: "approval-event-1",
      decision: "allow",
    }));
    const controlResult = await controlPromise;
    expect(controlResult).toMatchObject({
      type: "control_result",
      action: "approval.resolve",
      requestId: "approval-event-1",
      decision: "allow",
      succeeded: true,
    });
    const resolution = await resolutionPromise;
    expect(resolution).toMatchObject({
      type: "event",
      eventType: "approval.resolved",
      status: "approved",
      requestId: "approval-event-1",
      decision: "allow",
      requiresAction: false,
    });

    const response = await responsePromise;
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      status: "resolved",
      decision: "allow",
      requestId: "approval-event-1",
    });
    socket.close();
  });

  it("rejects unsupported live event types", async () => {
    const response = await fetch(`http://127.0.0.1:${server.port}/events`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ eventType: "tool.input.raw", source: "codex" }),
    });

    expect(response.status).toBe(400);
    expect(await response.json()).toMatchObject({ error: "unsupported eventType" });
  });

  it("exposes the configured port in the implementation", () => {
    expect(PORT).toBe(1122);
  });
});

function waitForOpen(socket) {
  return new Promise((resolve, reject) => {
    socket.once("open", resolve);
    socket.once("error", reject);
  });
}

function waitForMessage(socket) {
  return new Promise((resolve, reject) => {
    socket.once("message", (data) => resolve(data.toString("utf8")));
    socket.once("error", reject);
  });
}

function waitForPayload(socket, predicate) {
  return new Promise((resolve, reject) => {
    const onMessage = (data) => {
      let payload;
      try {
        payload = JSON.parse(data.toString("utf8"));
      } catch {
        return;
      }
      if (!predicate(payload)) return;
      cleanup();
      resolve(payload);
    };
    const onError = (error) => {
      cleanup();
      reject(error);
    };
    const cleanup = () => {
      socket.off("message", onMessage);
      socket.off("error", onError);
    };
    socket.on("message", onMessage);
    socket.on("error", onError);
  });
}

function silentLogger() {
  return {
    log() {},
    error() {},
  };
}
