import WebSocket from 'ws';

const WS_URL = 'ws://127.0.0.1:1122';
const HTTP_URL = 'http://127.0.0.1:1122/send';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const received = [];
const ws = new WebSocket(WS_URL);

const cases = [
  { label: '任务完成', status: 'completed', taskId: 'wb-state-done-1', tokenCount: 8800, durationMs: 120000 },
  { label: '任务失败', status: 'failed', taskId: 'wb-state-fail-1', tokenCount: 1234, durationMs: 65000 },
  { label: '任务待处理', status: 'waiting', taskId: 'wb-state-wait-1', tokenCount: 0, durationMs: 0 },
];

ws.on('open', async () => {
  console.log('[1] WS connected');
  for (const c of cases) {
    const body = {
      message: 'WorkBuddy ' + c.label,
      source: 'workbuddy',
      status: c.status,
      taskId: c.taskId,
      sessionId: 'test-session-123',
      cwd: 'C:\\work\\test-project',
      projectName: '测试项目A',
      tokenCount: c.tokenCount,
      durationMs: c.durationMs,
    };
    try {
      const resp = await fetch(HTTP_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const txt = await resp.text();
      console.log(`[2] POST ${c.label} -> HTTP ${resp.status} ${txt}`);
    } catch (e) {
      console.log('[2] POST error:', e.message);
    }
    await sleep(400);
  }
  await sleep(1500);

  console.log('[3] Captured broadcasts: ' + received.length);
  const expected = new Set(cases.map((c) => c.status));
  const got = new Set(received.map((m) => m.status));
  for (const status of expected) {
    const m = received.find((x) => x.status === status);
    if (!m) {
      console.log(`    [${status}] MISSING (FAIL)`);
      continue;
    }
    const token = m.tokenCount > 0 ? m.tokenCount : '(hidden)';
    const dur = m.durationMs > 0 ? m.durationMs + 'ms' : '(hidden)';
    console.log(`    [${status}] OK  source=${m.source} projectName=${m.projectName || '(none)'} duration=${dur} token=${token} sessionId=${m.sessionId || '(none)'}`);
  }
  const allOk = expected.size === got.size && cases.every((c) => received.some((m) => m.status === c.status));
  console.log('[4] THREE-STATE RESULT: ' + (allOk ? 'PASS' : 'FAIL'));

  ws.close();
  process.exit(0);
});

ws.on('message', (data) => {
  try {
    const msg = JSON.parse(data.toString());
    if (msg.type === 'message' && (msg.source === 'workbuddy' || msg.source === 'workbuddy-remote')) {
      received.push(msg);
    }
  } catch { /* ignore */ }
});

ws.on('error', (e) => { console.log('WS error:', e.message); process.exit(1); });
setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 20000);
