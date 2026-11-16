// 开发用：无依赖截图（Chrome DevTools Protocol），截图前自动脱敏用户名/本机路径
// 用法：node scripts/dev-screenshot.mjs <url> <out.png> [height]
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const CHROME_CANDIDATES = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
];
const url = process.argv[2] || 'http://127.0.0.1:17321';
const out = path.resolve(process.argv[3] || 'docs/screenshot.png');
const height = Number(process.argv[4] || 1000);
const PORT = 9333 + Math.floor(Math.random() * 200);

const chrome = await (async () => {
  for (const p of CHROME_CANDIDATES) {
    try {
      await fs.access(p);
      return p;
    } catch {}
  }
  throw new Error('未找到 Chrome/Edge');
})();

const profile = path.join(os.tmpdir(), `wb-shot-${Date.now()}`);
const child = spawn(
  chrome,
  [
    '--headless=new',
    '--disable-gpu',
    '--no-sandbox',
    '--hide-scrollbars',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    '--window-size=1440,' + height,
    'about:blank',
  ],
  { stdio: 'ignore' }
);

async function waitForDevtools() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/version`);
      if (res.ok) return await res.json();
    } catch {}
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error('DevTools 未就绪');
}

const version = await waitForDevtools();
const target = await (await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' })).json();
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  ws.onopen = resolve;
  ws.onerror = reject;
});

let id = 0;
const pending = new Map();
ws.onmessage = (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const { resolve, reject } = pending.get(msg.id);
    pending.delete(msg.id);
    if (msg.error) reject(new Error(msg.error.message));
    else resolve(msg.result);
  }
};
function send(method, params = {}) {
  const msgId = ++id;
  ws.send(JSON.stringify({ id: msgId, method, params }));
  return new Promise((resolve, reject) => pending.set(msgId, { resolve, reject }));
}

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: 1440,
  height,
  deviceScaleFactor: 1,
  mobile: false,
});

// 等待真实数据渲染完成（版本行出现），避免截到 loading 骨架
let ready = false;
for (let i = 0; i < 60; i++) {
  const r = await send('Runtime.evaluate', {
    expression: `(() => {
      const rows = document.querySelectorAll('#versionRows tr').length;
      const cards = document.querySelectorAll('[data-app-card]').length;
      return rows > 0 && cards > 0;
    })()`,
    returnByValue: true,
  });
  if (r.result?.value) {
    ready = true;
    break;
  }
  await new Promise((r2) => setTimeout(r2, 300));
}
if (!ready) console.warn('页面数据未在 18s 内就绪，仍继续截图');
await new Promise((r) => setTimeout(r, 500));

const sanitize = `
(() => {
  const user = ${JSON.stringify(process.env.USERNAME || '')};
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let n;
  while ((n = walker.nextNode())) {
    let t = n.nodeValue;
    t = t.replace(/C:\\\\Users\\\\[^\\\\\\s]+/gi, 'C:\\\\Users\\\\<user>');
    if (user) t = t.split(user).join('<user>');
    n.nodeValue = t;
  }
  return true;
})()`;
await send('Runtime.evaluate', { expression: sanitize, returnByValue: true });
await new Promise((r) => setTimeout(r, 300));

const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
await fs.mkdir(path.dirname(out), { recursive: true });
await fs.writeFile(out, Buffer.from(shot.data, 'base64'));
console.log(`saved ${out} (${version.Browser})`);

ws.close();
child.kill();
setTimeout(() => process.exit(0), 300);
