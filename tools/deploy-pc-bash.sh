#!/bin/bash
# Deploy PC app changes to the running instance.
# Bash-channel equivalent of tools/package-pc.ps1, which cannot run in this sandbox
# (PowerShell channel cannot start dotnet; schtasks.exe / cmd.exe are blocked).
# Usage: bash tools/deploy-pc-bash.sh [snapshot-label]
set -euo pipefail

LABEL=${1:-rollback}
case "$LABEL" in
  ""|.|..|*/*|*\\*|*[!A-Za-z0-9._-]*)
    echo "Invalid snapshot label: $LABEL" >&2
    exit 1
    ;;
esac
cd /w/TapRelay/pc-app || exit 1
PUBLISH=/w/TapRelay/pc-app/publish
STAGING=/w/TapRelay/pc-app/obj/package-staging
BACKUPS=$PUBLISH/backups

purge_dir() {
  local t="$1"
  local empty="/tmp/taprelay-purge-$RANDOM"
  local rc=0
  if [ ! -d "$t" ]; then
    return 0
  fi
  mkdir -p "$empty"
  # robocopy returns 0-7 for successful sync/delete; only 8+ is failure.
  if MSYS_NO_PATHCONV=1 robocopy "$(cygpath -w "$empty")" "$(cygpath -w "$t")" \
      /MIR /NFL /NDL /NJH /NJS /NC /NS /NP /R:0 /W:0 >/dev/null 2>&1; then
    rc=0
  else
    rc=$?
  fi
  if [ "$rc" -ge 8 ]; then
    echo "FAIL purge (robocopy rc=$rc): $t" >&2
    rmdir "$empty" 2>/dev/null || true
    return 1
  fi
  if ! rmdir "$t" 2>/dev/null; then
    echo "FAIL purge (directory remains): $t" >&2
    rmdir "$empty" 2>/dev/null || true
    return 1
  fi
  echo "purged: $(basename "$t")"
  rmdir "$empty" 2>/dev/null || true
}

echo "=== STEP 1: publish ==="
if [ -d "$STAGING" ]; then
  purge_dir "$STAGING"
fi
if dotnet publish TapRelay.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -o obj/package-staging --no-restore \
    >/w/TapRelay/.workbuddy-ai/tmp-publish.log 2>&1; then
  :
else
  echo "PUBLISH FAILED" >&2
  tail -5 /w/TapRelay/.workbuddy-ai/tmp-publish.log >&2
  exit 1
fi
NEW=$(sha256sum "$STAGING/TapRelay.exe" | cut -d' ' -f1)
SIZE=$(stat -c%s "$STAGING/TapRelay.exe")
echo "new exe: ${NEW:0:16} ($SIZE bytes)"
if [ "$SIZE" -lt 1000000 ]; then echo "FAIL: staged exe too small"; exit 1; fi

echo "=== STEP 2: snapshot backup ==="
OLD=$(sha256sum "$PUBLISH/TapRelay.exe" | cut -d' ' -f1)
TS=$(date +%Y%m%d-%H%M%S)
DEST="$BACKUPS/${TS}-${OLD:0:8}-${LABEL}"
mkdir -p "$DEST"
cp "$PUBLISH/TapRelay.exe" "$DEST/"
cp "$PUBLISH/TapRelay.pdb" "$DEST/" 2>/dev/null
cp "$PUBLISH/install-pc.ps1" "$DEST/" 2>/dev/null
SNAP=$(sha256sum "$DEST/TapRelay.exe" | cut -d' ' -f1)
echo "snapshot: ${SNAP:0:16} (dir $(basename "$DEST"))"
if [ "$SNAP" != "$OLD" ]; then echo "FAIL: snapshot hash mismatch, aborting"; exit 1; fi

echo "=== STEP 3: stop running app ==="
# `|| true` 不能省：本脚本是 set -euo pipefail，而 grep 在「没有匹配」时返回 1，
# 于是 `PID=$(tasklist | grep -i taprelay | ...)` 这个赋值会直接把脚本判失败。
# 实测症状：应用没在运行时，脚本在 STEP 3 静默中止，staging 已构建、快照已建立，
# 但 publish 里的 exe 从未被替换，也没有裁剪备份 —— 看起来像"部署成功"其实没部署。
PID=$(tasklist 2>/dev/null | grep -i taprelay | awk '{print $2}' | head -1 || true)
if [ -n "$PID" ]; then
  MSYS_NO_PATHCONV=1 taskkill /F /PID "$PID" >/dev/null 2>&1
  sleep 3
  echo "stopped PID $PID"
else
  echo "no running process"
fi
if tasklist 2>/dev/null | grep -qi taprelay; then echo "FAIL: process still alive"; exit 1; fi

echo "=== STEP 4: replace ==="
cp "$STAGING/TapRelay.exe" "$PUBLISH/TapRelay.exe"
cp "$STAGING/TapRelay.pdb" "$PUBLISH/TapRelay.pdb" 2>/dev/null
DEPLOYED=$(sha256sum "$PUBLISH/TapRelay.exe" | cut -d' ' -f1)
echo "deployed: ${DEPLOYED:0:16}"
if [ "$DEPLOYED" != "$NEW" ]; then echo "FAIL: deployed hash mismatch"; exit 1; fi

echo "=== STEP 5: build-info ==="
C:/Users/Admin/.workbuddy-ai/binaries/python/versions/3.13.12/python.exe -c "
import json, hashlib
sha = hashlib.sha256(open(r'W:\TapRelay\pc-app\publish\TapRelay.exe','rb').read()).hexdigest()
info = {'builtAt': __import__('datetime').datetime.now().strftime('%Y-%m-%dT%H:%M:%S+08:00'),
 'configuration':'Release','runtime':'win-x64','selfContained':True,'singleFile':True,
 'project':'W:' + chr(92) + 'TapRelay' + chr(92) + 'pc-app' + chr(92) + 'TapRelay.csproj',
 'output':'W:' + chr(92) + 'TapRelay' + chr(92) + 'pc-app' + chr(92) + 'publish',
 'sha256':sha,'symbols':'TapRelay.pdb'}
open(r'W:\TapRelay\pc-app\publish\build-info.json','w',encoding='utf-8').write(json.dumps(info, indent=2))
print('sha256', sha[:16])
"

echo "=== STEP 6: trim backups to 5 ==="
# oldest-first by mtime; keep the newest 5
mapfile -t KEEP < <(ls -1dt "$BACKUPS"/*/ | head -5)
mapfile -t ALL < <(ls -1dt "$BACKUPS"/*/)
for d in "${ALL[@]}"; do
  keep=0
  for k in "${KEEP[@]}"; do [ "$d" = "$k" ] && keep=1; done
  if [ $keep -eq 0 ]; then purge_dir "${d%/}"; fi
done
echo "remaining snapshots: $(ls -1d "$BACKUPS"/*/ | wc -l)"

echo "=== STEP 7: clean staging ==="
[ -d "$STAGING" ] && purge_dir "$STAGING"

echo "=== DONE ==="
echo "old: ${OLD:0:16}"
echo "new: ${NEW:0:16}"
