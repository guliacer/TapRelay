import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";

const APP_KEY_PATTERN = /^[a-z0-9][a-z0-9._-]{0,63}$/i;
const BRAND_COLOR_PATTERN = /^#[0-9a-f]{6}$/i;
const MAX_ICON_BYTES = 512 * 1024;
const RESERVED_KEYS = new Set([
  "claude", "claude-haha", "codex", "trae", "trae-solo", "traework", "zcode", "doubao", "qwen",
  "suyan", "baidunetdisk", "quarknetdisk", "quarknetdisk-upload", "adrive", "adrive-upload",
  "workbuddy", "comfyui", "opencode", "deepseekharness", "freebuff", "leigod", "taprelay",
]);

export function registerApp(apps, input) {
  const appKey = String(input?.appKey ?? "").trim().toLowerCase();
  const displayName = String(input?.displayName ?? "").trim();
  const brandColor = String(input?.brandColor ?? "").trim().toUpperCase();
  const iconBase64 = stripDataUrlPrefix(String(input?.iconBase64 ?? "").trim());

  if (!APP_KEY_PATTERN.test(appKey)) {
    throw new Error("appKey must match [a-z0-9][a-z0-9._-]{0,63}");
  }
  if (RESERVED_KEYS.has(appKey)) {
    throw new Error("appKey is reserved by the built-in application catalog");
  }
  if (displayName.length < 1 || displayName.length > 80) {
    throw new Error("displayName must be 1..80 characters");
  }
  if (!BRAND_COLOR_PATTERN.test(brandColor)) {
    throw new Error("brandColor must be a #RRGGBB value");
  }

  const icon = decodePng(iconBase64);
  const app = { appKey, displayName, brandColor, iconBase64 };
  apps.set(appKey, { ...app, icon });
  return app;
}

export function loadApps(registryPath) {
  const apps = new Map();
  if (!registryPath || !existsSync(registryPath)) return apps;
  try {
    const document = JSON.parse(readFileSync(registryPath, "utf8"));
    for (const item of document?.apps ?? []) {
      try {
        registerApp(apps, item);
      } catch {
        // Ignore one damaged entry instead of blocking the server startup.
      }
    }
  } catch {
    // A damaged registry is treated as an empty registry.
  }
  return apps;
}

export function saveApps(registryPath, apps) {
  if (!registryPath) return;
  mkdirSync(path.dirname(registryPath), { recursive: true });
  const payload = {
    apps: [...apps.values()].map(({ appKey, displayName, brandColor, iconBase64 }) => ({
      appKey,
      displayName,
      brandColor,
      iconBase64,
    })),
  };
  const tempPath = `${registryPath}.tmp`;
  try {
    writeFileSync(tempPath, JSON.stringify(payload), "utf8");
    copyFileSync(tempPath, registryPath);
  } finally {
    rmSync(tempPath, { force: true });
  }
}

export function listApps(apps) {
  return [...apps.values()]
    .sort((left, right) => left.appKey.localeCompare(right.appKey))
    .map(toPublicApp);
}

export function getAppIcon(apps, appKey) {
  const key = String(appKey ?? "").trim().toLowerCase();
  return apps.get(key)?.icon ?? null;
}

function toPublicApp(app) {
  return {
    appKey: app.appKey,
    displayName: app.displayName,
    brandColor: app.brandColor,
    iconPath: `/apps/icon/${encodeURIComponent(app.appKey)}`,
  };
}

function decodePng(value) {
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(value) || value.length === 0) {
    throw new Error("iconBase64 must be valid base64 PNG data");
  }
  const bytes = Buffer.from(value, "base64");
  const isPng = bytes.length >= 24 &&
    bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  if (!isPng || bytes.length > MAX_ICON_BYTES || bytes.readUInt32BE(16) !== 256 || bytes.readUInt32BE(20) !== 256) {
    throw new Error("iconBase64 must contain a 256x256 PNG no larger than 512 KiB");
  }
  return bytes;
}

function stripDataUrlPrefix(value) {
  const prefix = "data:image/png;base64,";
  return value.toLowerCase().startsWith(prefix) ? value.slice(prefix.length) : value;
}
