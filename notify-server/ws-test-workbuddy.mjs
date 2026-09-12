import WebSocket from 'ws';

const WS_URL = 'ws://127.0.0.1:1122';
const HTTP_URL = 'http://127.0.0.1:1122/send';

let broadcastReceived = null;
let resumeResponse = null;

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

  // ---- Test B: 手机"继续"指令 (agent.resume control) ----
  // 手机端实际发送的是线路别名 workbuddy-remote（见 NotifyServer.ToWireSource / Android message.source），
  // 这里用真实来源值验证 PC 端 NormalizeSource 能归并回 workbuddy 并正确解析脚本。
  const control = {
    type: 'control',
    action: 'agent.resume',
    source: 'workbuddy-remote',
    sessionId: 'test-session-123',
    cwd: 'C:\\work\\test-project',
  };
  ws.send(JSON.stringify(control));
  console.log('[4] SENT control agent.resume (source=workbuddy-remote)');

  await sleep(8000); // resume-workbuddy.ps1 最多 5s 启动窗口
  if (resumeResponse) {
    console.log('[5] RESUME control_result:');
    console.log('    succeeded =', resumeResponse.succeeded);
    console.log('    source    =', resumeResponse.source);
    console.log('    status    =', resumeResponse.status);
    console.log('    error     =', resumeResponse.error || '(none)');
  } else {
    console.log('[5] RESUME control_result: NONE (FAIL)');
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
  if (msg.type === 'control_result' && msg.action === 'agent.resume') {
    resumeResponse = msg;
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
