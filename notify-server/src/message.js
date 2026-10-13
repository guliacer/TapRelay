import { MAX_MESSAGE_LENGTH } from "./config.js";

export const MAX_TASK_LOG_LENGTH = 4000;
const MAX_REQUEST_ID_LENGTH = 100;
const MAX_NOTIFICATION_TITLE_LENGTH = 120;
const MAX_ACTION_URL_LENGTH = 2048;
const MAX_CREDITS_LENGTH = 64;

export function normalizeMessage(message) {
  if (typeof message !== "string") {
    throw new Error("message must be a string");
  }

  const trimmed = message.trim();
  if (trimmed.length === 0) {
    throw new Error("message must not be empty");
  }

  if (trimmed.length > MAX_MESSAGE_LENGTH) {
    throw new Error(`message must be <= ${MAX_MESSAGE_LENGTH} characters`);
  }

  return trimmed;
}

export function createMessage(content, metadata = {}, date = new Date()) {
  return {
    type: "message",
    content,
    time: formatIsoWithOffset(date),
    source: metadata.source ?? "",
    status: metadata.status ?? "",
    taskId: metadata.taskId ?? "",
    cwd: metadata.cwd ?? "",
    durationMs: normalizeDurationMs(metadata.durationMs),
    tokenCount: normalizeTokenCount(metadata.tokenCount),
    credits: normalizeCredits(metadata.credits),
    sessionId: normalizeSessionId(metadata.sessionId),
    taskLog: normalizeTaskLog(metadata.taskLog),
    notificationTitle: normalizeString(metadata.notificationTitle, MAX_NOTIFICATION_TITLE_LENGTH),
    actionUrl: normalizeString(metadata.actionUrl, MAX_ACTION_URL_LENGTH),
    requestId: normalizeString(metadata.requestId, MAX_REQUEST_ID_LENGTH),
    requiresAction: metadata.requiresAction === true ||
      String(metadata.status ?? "").trim().toLowerCase() === "waiting",
  };
}

/** Task detail shown by the Android card; empty means the content is used. */
export function normalizeTaskLog(value) {
  if (typeof value !== "string" || !value.trim()) return "";
  let length = Math.min(value.length, MAX_TASK_LOG_LENGTH);
  if (length < value.length &&
      /[\uD800-\uDBFF]/.test(value[length - 1]) && /[\uDC00-\uDFFF]/.test(value[length])) {
    length--;
  }
  return value.slice(0, length);
}

export function normalizeDurationMs(value) {
  const maxDurationMs = 30 * 24 * 60 * 60 * 1000;
  return Number.isSafeInteger(value) && value > 0 && value <= maxDurationMs ? value : 0;
}

/** 仅正整数 token；无法可靠获取时为 0（手机端不展示）。 */
export function normalizeTokenCount(value) {
  const maxTokenCount = 2_000_000_000;
  return Number.isSafeInteger(value) && value > 0 && value <= maxTokenCount ? value : 0;
}

/** TraeCode CN usage summary; keep the original text but cap its wire size. */
export function normalizeCredits(value) {
  return typeof value === "string" ? value.trim().slice(0, MAX_CREDITS_LENGTH) : "";
}

/** 会话 ID 用于任务关联和停止控制；去空白并截断到 160 字符。 */
function normalizeSessionId(value) {
  const maxSessionIdLength = 160;
  return typeof value === "string" ? value.trim().slice(0, maxSessionIdLength) : "";
}

function normalizeString(value, maxLength) {
  return typeof value === "string" ? value.trim().slice(0, maxLength) : "";
}

export function formatIsoWithOffset(date = new Date()) {
  const pad = (value) => String(value).padStart(2, "0");
  const year = date.getFullYear();
  const month = pad(date.getMonth() + 1);
  const day = pad(date.getDate());
  const hours = pad(date.getHours());
  const minutes = pad(date.getMinutes());
  const seconds = pad(date.getSeconds());
  const offsetMinutes = -date.getTimezoneOffset();
  const sign = offsetMinutes >= 0 ? "+" : "-";
  const absoluteOffset = Math.abs(offsetMinutes);
  const offsetHours = pad(Math.floor(absoluteOffset / 60));
  const offsetRemainingMinutes = pad(absoluteOffset % 60);

  return `${year}-${month}-${day}T${hours}:${minutes}:${seconds}${sign}${offsetHours}:${offsetRemainingMinutes}`;
}
