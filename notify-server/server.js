import { createServer } from "node:http";
import { fileURLToPath } from "node:url";
import path from "node:path";
import WebSocket, { WebSocketServer } from "ws";
import {
  HOST,
  PORT,
  HEARTBEAT_INTERVAL_MS,
  HEALTH_PATH,
  SEND_PATH,
  EVENTS_PATH,
  MAX_REQUEST_BODY_BYTES,
  MAX_EVENT_BODY_BYTES,
  MAX_APP_REQUEST_BODY_BYTES,
} from "./src/config.js";
import { createMessage, normalizeMessage, normalizeTaskLog } from "./src/message.js";
import { createResolution, normalizeEvent, toEventPayload } from "./src/event.js";
import { getLanIPv4Addresses, formatAddressList } from "./src/network.js";
import { getAppIcon, listApps, loadApps, registerApp, saveApps } from "./src/apps.js";
import os from "node:os";

export async function startNotifyServer(options = {}) {
  const host = options.host ?? HOST;
  const port = options.port ?? PORT;
  const logger = options.logger ?? console;

  const clients = new Set();
  const appRegistryPath = options.appRegistryPath === undefined
    ? path.join(process.env.LOCALAPPDATA ?? os.homedir(), "LanNotify", "notification-app-registry.json")
    : options.appRegistryPath;
  const apps = loadApps(appRegistryPath);
  const pendingApprovals = new Map();
  const recentEvents = new Map();
  let heartbeatTimer = null;

  const httpServer = createServer(async (req, res) => {
    try {
      await handleRequest(req, res, {
        clients,
        apps,
        pendingApprovals,
        recentEvents,
        saveApps: () => saveApps(appRegistryPath, apps),
        logger,
      });
    } catch (error) {
      logger.error?.(error);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
      }
      res.end(JSON.stringify({ error: "internal server error" }));
    }
  });

  const wss = new WebSocketServer({ server: httpServer });

  wss.on("connection", (socket, request) => {
    socket.isAlive = true;
    clients.add(socket);
    logger.log?.(`[ws] connected ${request.socket.remoteAddress ?? "unknown"}`);

    socket.on("pong", () => {
      socket.isAlive = true;
    });

    socket.on("message", (data) => {
      void handleClientMessage(socket, data, logger, pendingApprovals);
    });

    socket.on("close", () => {
      clients.delete(socket);
    });

    socket.on("error", () => {
      clients.delete(socket);
    });
  });

  heartbeatTimer = setInterval(() => {
    for (const socket of clients) {
      if (socket.isAlive === false) {
        clients.delete(socket);
        socket.terminate();
        continue;
      }
      socket.isAlive = false;
      socket.ping();
    }
  }, HEARTBEAT_INTERVAL_MS);

  await new Promise((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(port, host, resolve);
  });

  const address = httpServer.address();
  const actualPort = typeof address === "object" && address ? address.port : port;

  logger.log?.(`[server] listening on http://${host}:${actualPort}`);
  for (const lanAddress of getLanIPv4Addresses()) {
    logger.log?.(`[server] lan ${lanAddress}:${actualPort}`);
  }

  return {
    httpServer,
    wss,
    clients,
    host,
    port: actualPort,
    close: async () => {
      if (heartbeatTimer) {
        clearInterval(heartbeatTimer);
        heartbeatTimer = null;
      }
      for (const socket of clients) {
        try {
          socket.terminate();
        } catch {
          // ignore
        }
      }
      clients.clear();
      await new Promise((resolve) => wss.close(() => resolve()));
      await new Promise((resolve) => httpServer.close(() => resolve()));
    },
  };
}

async function handleClientMessage(socket, data, logger, pendingApprovals) {
  let payload;
  try {
    payload = JSON.parse(data.toString("utf8"));
  } catch {
    logger.warn?.("[ws] ignored invalid json message");
    return;
  }

  if (payload?.type === "heartbeat" && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ type: "heartbeat_ack" }));
    return;
  }

  if (payload?.type === "control" && payload.action === "approval.resolve") {
    const requestId = typeof payload.requestId === "string" ? payload.requestId.trim().slice(0, 100) : "";
    const decision = typeof payload.decision === "string" ? payload.decision.trim().toLowerCase() : "";
    let response;
    if (!requestId || !["allow", "deny"].includes(decision)) {
      response = {
        type: "control_result",
        action: "approval.resolve",
        requestId,
        succeeded: false,
        error: "requestId and decision allow|deny are required",
      };
    } else {
      const pending = pendingApprovals.get(requestId);
      const accepted = pending?.resolve(decision) === true;
      response = {
        type: "control_result",
        action: "approval.resolve",
        requestId,
        ...(accepted ? { decision } : {}),
        succeeded: accepted,
        ...(accepted ? {} : { error: "approval request is missing or already resolved" }),
      };
    }
    if (socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify(response));
    }
  }
}

async function handleRequest(req, res, context) {
  const {
    clients,
    apps,
    pendingApprovals,
    recentEvents,
    saveApps: persistApps,
  } = context;
  const url = new URL(req.url ?? "/", "http://localhost");

  if (req.method === "GET" && url.pathname === HEALTH_PATH) {
    sendJson(res, 200, {
      status: "ok",
      port: (req.socket?.localPort ?? PORT),
      clients: clients.size,
    });
    return;
  }

  if (url.pathname === "/apps") {
    if (req.method !== "GET") {
      sendJson(res, 405, { error: "method not allowed" });
      return;
    }
    sendJson(res, 200, { apps: listApps(context.apps) });
    return;
  }

  if (url.pathname === "/apps/register") {
    if (req.method !== "POST") {
      sendJson(res, 405, { error: "method not allowed" });
      return;
    }
    if (!isJsonRequest(req)) {
      sendJson(res, 415, { error: "content-type must be application/json" });
      return;
    }
    try {
      const body = await readJsonBody(req, MAX_APP_REQUEST_BODY_BYTES);
      const app = registerApp(context.apps, body);
      persistApps?.();
      sendJson(res, 200, { status: "registered", app: listApps(context.apps).find((item) => item.appKey === app.appKey) });
    } catch (error) {
      sendJson(res, 400, { error: error instanceof Error ? error.message : "invalid app registration JSON" });
    }
    return;
  }

  if (req.method === "GET" && url.pathname.startsWith("/apps/icon/")) {
    const appKey = decodeURIComponent(url.pathname.slice("/apps/icon/".length));
    const icon = getAppIcon(context.apps, appKey);
    if (!icon) {
      sendJson(res, 404, { error: "icon not found" });
      return;
    }
    res.writeHead(200, { "Content-Type": "image/png", "Cache-Control": "no-cache" });
    res.end(icon);
    return;
  }

  if (url.pathname === SEND_PATH) {
    if (req.method !== "POST") {
      sendJson(res, 405, { error: "method not allowed" });
      return;
    }

    if (!isJsonRequest(req)) {
      sendJson(res, 415, { error: "content-type must be application/json" });
      return;
    }

    let body;
    let normalizedMessage;
    try {
      body = await readJsonBody(req, MAX_REQUEST_BODY_BYTES);
      normalizedMessage = normalizeMessage(body?.message);
    } catch {
      sendJson(res, 400, { error: "message must be a non-empty string with max length 500" });
      return;
    }

    const source = typeof body?.source === "string" ? body.source.trim().slice(0, 40) : "";
    const status = typeof body?.status === "string" ? body.status.trim().slice(0, 20) : "";
    const taskId = typeof body?.taskId === "string" ? body.taskId.trim().slice(0, 200) : "";
    const cwd = typeof body?.cwd === "string" ? body.cwd.trim().slice(0, 240) : "";
    const durationMs = Number.isSafeInteger(body?.durationMs) ? body.durationMs : 0;
    const tokenCount = Number.isSafeInteger(body?.tokenCount) ? body.tokenCount : 0;
    const sessionId = typeof body?.sessionId === "string" ? body.sessionId.trim().slice(0, 160) : "";
    const taskLog = normalizeTaskLog(body?.taskLog);
    const credits = typeof body?.credits === "string"
      ? body.credits.trim().slice(0, 64)
      : "";
    const notificationTitle = typeof body?.notificationTitle === "string"
      ? body.notificationTitle.trim().slice(0, 120)
      : "";
    const actionUrl = typeof body?.actionUrl === "string"
      ? body.actionUrl.trim().slice(0, 2048)
      : "";
    const requestId = typeof body?.requestId === "string"
      ? body.requestId.trim().slice(0, 100)
      : "";
    const requiresAction = body?.requiresAction === true || status.toLowerCase() === "waiting";
    const payload = createMessage(
      source ? `[${source}] ${normalizedMessage}` : normalizedMessage,
      {
        source,
        status,
        taskId,
        cwd,
        durationMs,
        tokenCount,
        credits,
        sessionId,
        taskLog,
        notificationTitle,
        actionUrl,
        requestId,
        requiresAction,
      },
    );
    broadcast(clients, payload);
    sendJson(res, 200, { status: "sent", clients: clients.size });
    return;
  }

  if (url.pathname === EVENTS_PATH) {
    await handleEventRequest(req, res, context, pendingApprovals, recentEvents);
    return;
  }

  sendJson(res, 404, { error: "not found" });
}

async function handleEventRequest(req, res, context, pendingApprovals, recentEvents) {
  if (req.method !== "POST") {
    sendJson(res, 405, { error: "method not allowed" });
    return;
  }
  if (!isJsonRequest(req)) {
    sendJson(res, 415, { error: "content-type must be application/json" });
    return;
  }

  let event;
  try {
    event = normalizeEvent(await readJsonBody(req, MAX_EVENT_BODY_BYTES));
  } catch (error) {
    sendJson(res, 400, { error: error instanceof Error ? error.message : "invalid event request" });
    return;
  }

  const dedupKey = `${event.source}:${event.eventType}:${event.eventId}`;
  const now = Date.now();
  for (const [key, seenAt] of recentEvents) {
    if (now - seenAt > 120_000) recentEvents.delete(key);
  }
  if (recentEvents.has(dedupKey)) {
    sendJson(res, 200, { status: "duplicate", eventId: event.eventId });
    return;
  }
  recentEvents.set(dedupKey, now);

  if (event.eventType !== "approval.requested") {
    const sent = broadcast(context.clients, toEventPayload(event));
    sendJson(res, 200, { status: "sent", eventId: event.eventId, clients: sent });
    return;
  }

  if (pendingApprovals.has(event.requestId)) {
    sendJson(res, 409, { error: "approval request already exists", requestId: event.requestId });
    return;
  }

  const pending = createPendingApproval(event, pendingApprovals);
  const sent = broadcast(context.clients, toEventPayload(event));
  if (sent === 0) {
    pendingApprovals.delete(event.requestId);
    sendJson(res, 200, {
      status: "unavailable",
      decision: "unavailable",
      requestId: event.requestId,
    });
    return;
  }

  try {
    const decision = await waitForApproval(req, pending, event.expiresAt);
    const resolution = createResolution(event, decision ?? "deny");
    broadcast(context.clients, toEventPayload(resolution));
    sendJson(res, 200, {
      status: decision ? "resolved" : "expired",
      decision: decision ?? "deny",
      requestId: event.requestId,
    });
  } finally {
    pendingApprovals.delete(event.requestId);
  }
}

function createPendingApproval(event, pendingApprovals) {
  let settled = false;
  let resolveDecision;
  const promise = new Promise((resolve) => {
    resolveDecision = (decision) => {
      if (settled) return false;
      settled = true;
      resolve(decision);
      return true;
    };
  });
  const pending = { event, promise, resolve: resolveDecision };
  pendingApprovals.set(event.requestId, pending);
  return pending;
}

function waitForApproval(req, pending, expiresAt) {
  const timeoutMs = Math.max(1, expiresAt - Date.now());
  return new Promise((resolve) => {
    let settled = false;
    let timeoutId;
    const onAborted = () => finish(null);
    const finish = (decision) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeoutId);
      req.off("aborted", onAborted);
      resolve(decision);
    };

    if (req.aborted) {
      finish(null);
      return;
    }
    req.once("aborted", onAborted);
    timeoutId = setTimeout(() => finish(null), timeoutMs);
    pending.promise.then(finish);
  });
}

function broadcast(clients, payload) {
  const data = JSON.stringify(payload);
  let sent = 0;
  for (const socket of clients) {
    if (socket.readyState === WebSocket.OPEN) {
      socket.send(data);
      sent++;
    }
  }
  return sent;
}

function sendJson(res, statusCode, body) {
  res.writeHead(statusCode, { "Content-Type": "application/json; charset=utf-8" });
  res.end(JSON.stringify(body));
}

function isJsonRequest(req) {
  const contentType = req.headers["content-type"] ?? "";
  return contentType.includes("application/json");
}

function readJsonBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let totalBytes = 0;
    let settled = false;

    const fail = (error) => {
      if (settled) {
        return;
      }
      settled = true;
      reject(error);
      req.destroy();
    };

    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => {
      if (settled) {
        return;
      }

      try {
        totalBytes = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
        if (totalBytes > maxBytes) {
          fail(new Error("request body too large"));
          return;
        }

        const text = Buffer.concat(chunks).toString("utf8").trim();
        settled = true;
        resolve(text ? JSON.parse(text) : {});
      } catch (error) {
        fail(error);
      }
    });
    req.on("error", fail);
  });
}

export function printStartupGuide(logger = console) {
  const addresses = getLanIPv4Addresses();
  logger.log?.("[guide] use one of the following IPs on the Android client:");
  logger.log?.(`[guide] ${formatAddressList(addresses, PORT)}`);
  logger.log?.(`[guide] curl POST http://localhost:${PORT}${SEND_PATH}`);
}

export async function main() {
  const server = await startNotifyServer({ logger: console });
  printStartupGuide(console);

  const shutdown = async () => {
    await server.close();
    process.exit(0);
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

const entryPoint = process.argv[1] ? path.resolve(process.argv[1]) : "";
if (fileURLToPath(import.meta.url) === entryPoint) {
  await main();
}
