import WebSocket from 'ws';

const WS_URL = 'ws://127.0.0.1:1122';
const HTTP_URL = 'http://127.0.0.1:1122/send';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const rawMessages = [];

const ws = new WebSocket(WS_URL);

ws.on('open', async () => {
  console.log('[1] WS connected');

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

  await sleep(2000);

  console.log('[3] RAW messages received (' + rawMessages.length + '):');
  for (const m of rawMessages) {
    console.log('    ---');
    console.log('    ' + m);
  }

  ws.close();
  process.exit(0);
});

ws.on('message', (data) => {
  const text = data.toString('utf8');
  rawMessages.push(text);
  console.log('[msg] received: ' + text.slice(0, 300));
});

ws.on('error', (e) => {
  console.log('WS error:', e.message);
  process.exit(1);
});

setTimeout(() => {
  console.log('TIMEOUT');
  process.exit(1);
}, 15000);
