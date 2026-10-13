import WebSocket from 'ws';

const WS_URL = 'ws://127.0.0.1:1122';
const HTTP_URL = 'http://127.0.0.1:1122/send';

let broadcastReceived = null;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const ws = new WebSocket(WS_URL);

ws.on('open', async () => {
  console.log('[1] WS connected');

  // ---- Test A: WorkBuddy 失败通知广播 ----
  const sendBody = {
    message: 'WorkBuddy 任务失败',
    source: 'workbuddy',
    status: 'failed',
    taskId: 'wb-task-001',
    sessionId: 'test-session-123',
    cwd: 'C:\\work\\test-project',
    projectName: '测试项目A',
    tokenCount: 1234,
    durationMs: 65000,
  };
  try {
    const resp = await fetch(HTTP_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(sendBody),
    });
    const txt = await resp.text();
    console.log(`[2] POST /send -> HTTP ${resp.status} ${txt}`);
  } catch (e) {
    console.log('[2] POST /send error:', e.message);
  }

  await sleep(1500);
  if (broadcastReceived) {
    console.log('[3] BROADCAST payload:');
    console.log('    source      =', broadcastReceived.source);
    console.log('    status      =', broadcastReceived.status);
    console.log('    taskId      =', broadcastReceived.taskId);
    console.log('    sessionId   =', broadcastReceived.sessionId);
    console.log('    projectName =', broadcastReceived.projectName);
    console.log('    durationMs  =', broadcastReceived.durationMs);
    console.log('    tokenCount  =', broadcastReceived.tokenCount);
    console.log('    content     =', broadcastReceived.content);
  } else {
    console.log('[3] BROADCAST: NONE (FAIL)');
  }

  ws.close();
  process.exit(0);
});

ws.on('message', (data) => {
  let msg;
  try {
    msg = JSON.parse(data.toString());
  } catch {
    return;
  }
  // PC 端广播时对 workbuddy 来源使用线路别名 workbuddy-remote（见 NotifyServer.ToWireSource），
  // 因此按 workbuddy-remote 匹配；同时保留 workbuddy 以兼容直连 /send 的原始来源。
  if (msg.type === 'message' && (msg.source === 'workbuddy' || msg.source === 'workbuddy-remote')) {
    broadcastReceived = msg;
  }
});

ws.on('error', (e) => {
  console.log('WS error:', e.message);
  process.exit(1);
});

setTimeout(() => {
  console.log('TIMEOUT');
  process.exit(1);
}, 20000);
