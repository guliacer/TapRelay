import { randomUUID } from "node:crypto";
import {
  formatIsoWithOffset,
  normalizeCredits,
  normalizeDurationMs,
  normalizeTaskLog,
  normalizeTokenCount,
} from "./message.js";

const MAX_EVENT_ID_LENGTH = 100;
const MAX_EVENT_TYPE_LENGTH = 40;
const MAX_SOURCE_LENGTH = 40;
const MAX_SESSION_ID_LENGTH = 160;
const MAX_TASK_ID_LENGTH = 200;
const MAX_PROJECT_NAME_LENGTH = 120;
const MAX_CWD_LENGTH = 240;
const MAX_TOOL_NAME_LENGTH = 120;
const MAX_TOOL_USE_ID_LENGTH = 160;
const MAX_MESSAGE_LENGTH = 500;
const MAX_INPUT_SUMMARY_LENGTH = 500;
const APPROVAL_TIMEOUT_MS = 5 * 60 * 1000;

const SUPPORTED_EVENT_TYPES = new Set([
  "session.started",
  "session.ended",
  "tool.started",
  "tool.finished",
  "tool.failed",
  "approval.requested",
  "approval.resolved",
  "subagent.started",
  "subagent.ended",
  "agent.status",
]);

export function normalizeEvent(body, now = Date.now()) {
  if (!body || typeof body !== "object" || Array.isArray(body)) {
    throw new Error("event body must be a JSON object");
  }

  const eventId = readString(body.eventId, MAX_EVENT_ID_LENGTH) || randomUUID().replaceAll("-", "");
  const eventType = (readString(body.eventType, MAX_EVENT_TYPE_LENGTH) ||
    readString(body.event, MAX_EVENT_TYPE_LENGTH)).toLowerCase();
  if (!SUPPORTED_EVENT_TYPES.has(eventType)) {
    throw new Error("unsupported eventType");
  }

  const source = readString(body.source, MAX_SOURCE_LENGTH).toLowerCase();
  if (!source) throw new Error("source is required");

  const event = {
    eventId,
    eventType,
    source,
    status: readString(body.status, 30).toLowerCase() || defaultStatus(eventType),
    sessionId: readString(body.sessionId, MAX_SESSION_ID_LENGTH),
    taskId: readString(body.taskId, MAX_TASK_ID_LENGTH),
    projectName: readString(body.projectName, MAX_PROJECT_NAME_LENGTH),
    cwd: readString(body.cwd, MAX_CWD_LENGTH),
    toolName: readString(body.toolName, MAX_TOOL_NAME_LENGTH) || readString(body.tool, MAX_TOOL_NAME_LENGTH),
    toolUseId: readString(body.toolUseId, MAX_TOOL_USE_ID_LENGTH),
    message: readString(body.message, MAX_MESSAGE_LENGTH) || readString(body.content, MAX_MESSAGE_LENGTH),
    taskLog: normalizeTaskLog(body.taskLog),
    durationMs: normalizeDurationMs(body.durationMs),
    tokenCount: normalizeTokenCount(body.tokenCount),
    credits: normalizeCredits(body.credits),
    inputSummary: readString(body.inputSummary, MAX_INPUT_SUMMARY_LENGTH),
    requestId: readString(body.requestId, MAX_EVENT_ID_LENGTH),
    requiresAction: body.requiresAction === true,
    expiresAt: readInteger(body.expiresAt),
    approvalDecision: readString(body.decision, 20).toLowerCase(),
    time: formatIsoWithOffset(new Date(now)),
  };

  if (!event.taskId) event.taskId = event.sessionId;
  if (event.status === "waiting") {
    // Waiting is always actionable; an absent requestId intentionally remains a
    // non-approval task that opens its details instead of faking allow/deny.
    event.requiresAction = true;
  }
  if (eventType === "approval.requested") {
    event.requiresAction = true;
    event.requestId ||= event.eventId;
    event.expiresAt = event.expiresAt > 0
      ? Math.min(event.expiresAt, now + APPROVAL_TIMEOUT_MS)
      : now + APPROVAL_TIMEOUT_MS;
    if (!event.message) {
      event.message = event.toolName ? `Approval required: ${event.toolName}` : "Approval required";
    }
  }

  return event;
}

export function toEventPayload(event) {
  return {
    type: "event",
    eventId: event.eventId,
    eventType: event.eventType,
    source: event.source,
    time: event.time,
    status: event.status,
    sessionId: event.sessionId,
    taskId: event.taskId,
    projectName: event.projectName,
    cwd: event.cwd,
    toolName: event.toolName,
    toolUseId: event.toolUseId,
    message: event.message,
    taskLog: event.taskLog,
    durationMs: event.durationMs,
    tokenCount: event.tokenCount,
    credits: event.credits,
    inputSummary: event.inputSummary,
    requestId: event.requestId,
    requiresAction: event.requiresAction,
    expiresAt: event.expiresAt,
    decision: event.approvalDecision,
  };
}

export function createResolution(event, decision) {
  const allowed = decision === "allow";
  return {
    ...event,
    eventId: `${event.eventId}:resolved`,
    eventType: "approval.resolved",
    status: allowed ? "approved" : "rejected",
    message: `${allowed ? "Allowed" : "Denied"}${event.toolName ? `: ${event.toolName}` : ""}`,
    requiresAction: false,
    approvalDecision: decision,
  };
}

function defaultStatus(eventType) {
  if (["session.started", "tool.started", "subagent.started"].includes(eventType)) return "running";
  if (eventType === "tool.failed") return "failed";
  if (eventType === "approval.requested") return "waiting";
  if (["session.ended", "tool.finished", "subagent.ended"].includes(eventType)) return "completed";
  return "processing";
}

function readString(value, maxLength) {
  return typeof value === "string" ? value.trim().slice(0, maxLength) : "";
}

function readInteger(value) {
  return Number.isSafeInteger(value) && value > 0 ? value : 0;
}
