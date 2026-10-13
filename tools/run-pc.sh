#!/usr/bin/env bash
# Start the PC app with a single-instance guarantee.
#
# Why this exists: launching a second TapRelay.exe does NOT create a second instance.
# The app owns a mutex (TapRelay.SingleInstance) and a named event
# (TapRelay.ExitSignal); the new process signals the running one and the old process
# exits through MainForm.ExitForNewInstance. That handover is graceful and is always
# preferred over taskkill.
#
# taskkill is only the fallback for a process stuck mid-shutdown. That case is real:
# host.StopAsync was observed to hang forever (the diagnostic log stops at
# "开始停止服务" with no completion line, and the orphaned process keeps the port).
# MainForm now bounds every teardown step and arms an exit watchdog, so a stuck
# process should no longer happen -- but this script still cleans up if it does.
#
# Usage:
#   tools/run-pc.sh              # hand over to a fresh instance and wait for /health
#   tools/run-pc.sh <label>      # same, label is only used in the log output
#
# NOTE: in the WorkBuddy sandbox a process started by a foreground script can be
# reaped when the script ends. For long-lived runs, invoke this script itself with
# run_in_background, or start the exe directly as a background task.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXE="$ROOT/pc-app/publish/TapRelay.exe"
LABEL="${1:-manual}"
HEALTH_URL="http://127.0.0.1:1122/health"
HEALTH_WAIT_SECONDS=25
FORCE_KILL_AFTER_SECONDS=8

if [ ! -x "$EXE" ]; then
  echo "ERROR: $EXE not found or not executable. Run tools/deploy-pc-bash.sh first." >&2
  exit 1
fi

list_pids() {
  # `|| true` matters: this script runs under `set -e` and grep exits 1 when there
  # is no match, which would abort the script right here.
  MSYS_NO_PATHCONV=1 tasklist 2>/dev/null | grep -i '^TapRelay\.exe' | awk '{print $2}' || true
}

OLD_PIDS="$(list_pids)"
if [ -n "$OLD_PIDS" ]; then
  echo "running instance(s): $(echo "$OLD_PIDS" | tr '\n' ' ')"
else
  echo "running instance(s): none"
fi

echo "launching new instance ($LABEL) ..."
"$EXE" &
NEW_LAUNCHER_PID=$!

# Give the app its startup budget. It performs the handover on its own.
deadline=$((SECONDS + HEALTH_WAIT_SECONDS))
healthy=0
while [ "$SECONDS" -lt "$deadline" ]; do
  if curl -s -m 2 --noproxy '*' "$HEALTH_URL" 2>/dev/null | grep -q '"status":"ok"'; then
    healthy=1
    break
  fi
  sleep 1
done

# A process stuck mid-shutdown never reaches the handover; force it out.
if [ -n "$OLD_PIDS" ]; then
  still_alive=""
  for pid in $OLD_PIDS; do
    if MSYS_NO_PATHCONV=1 tasklist /FI "PID eq $pid" 2>/dev/null | grep -q "$pid"; then
      still_alive="$still_alive $pid"
    fi
  done
  if [ -n "$still_alive" ]; then
    echo "old instance still alive after ${FORCE_KILL_AFTER_SECONDS}s:$still_alive -> forcing exit"
    sleep "$FORCE_KILL_AFTER_SECONDS"
    for pid in $still_alive; do
      MSYS_NO_PATHCONV=1 taskkill /F /PID "$pid" >/dev/null 2>&1 || true
    done
  fi
fi

if [ "$healthy" = "1" ]; then
  echo "health: $(curl -s -m 3 --noproxy '*' "$HEALTH_URL")"
  echo "pids: $(list_pids | tr '\n' ' ')"
else
  echo "WARNING: $HEALTH_URL did not become healthy within ${HEALTH_WAIT_SECONDS}s" >&2
  echo "pids: $(list_pids | tr '\n' ' ')" >&2
  exit 1
fi

wait "$NEW_LAUNCHER_PID" 2>/dev/null || true
