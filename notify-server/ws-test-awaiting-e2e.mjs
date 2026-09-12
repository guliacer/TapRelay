// E2E: 写入一条 WorkBuddy awaiting_input 日志，验证 TapRelay 广播 waiting 到 WebSocket。
import WebSocket from 'ws';
import fs from 'fs';

const WS_URL = 'ws://localhost:1122';
const TEST_FILE = process.env.TEST_FILE;
const TEST_LINE = process.env.TEST_LINE;

if (!TEST_FILE || !TEST_LINE) {
  console.error('need TEST_FILE and TEST_LINE');
  process.exit(2);
}

const ws = new WebSocket(WS_URL, { headers: { Origin: 'http://localhost:1122' } });
const received = [];
const timer = setTimeout(() => {
  console.error('TIMEOUT waiting for waiting broadcast');
  cleanup();
  process.exit(1);
}, 15000);

function cleanup() {
  clearTimeout(timer);
  try { ws.close(); } catch (e) {}
}

ws.on('open', () => {
  console.log('WS connected');
  // 写入测试日志行（追加）
  fs.appendFileSync(TEST_FILE, TEST_LINE + '\n');
  console.log('appended test line');
});

ws.on('message', (data) => {
  const text = data.toString();
  received.push(text);
  console.log('RECV:', text.slice(0, 400));
  try {
    const msg = JSON.parse(text);
    if (msg.type === 'message' || msg.type === 'event') {
      const status = msg.status || msg.eventType;
      if (msg.source === 'workbuddy' && (status === 'waiting')) {
        console.log('E2E PASS: waiting broadcast received');
        cleanup();
        process.exit(0);
      }
    }
  } catch (e) { /* ignore */ }
});

ws.on('error', (e) => {
  console.error('WS error:', e.message);
  cleanup();
  process.exit(1);
});
