import fs from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export const APPS = [
  {
    id: 'workbuddy',
    name: 'WorkBuddy',
    edition: '国内版',
    exe: 'WorkBuddy.exe',
    process: 'WorkBuddy.exe',
    endpoint: 'www.workbuddy.cn',
    accent: '#2f6bff',
  },
  {
    id: 'workbuddyai',
    name: 'WorkBuddy AI',
    edition: '国际版',
    exe: 'WorkBuddyAI.exe',
    process: 'WorkBuddyAI.exe',
    endpoint: 'www.workbuddy.ai',
    accent: '#7c4dff',
  },
];

export function appById(id) {
  return APPS.find((a) => a.id === id) || null;
}

function envPath(name) {
  return process.env[name] || '';
}

function scanRoots() {
  const roots = new Set();
  const add = (p) => {
    if (p && !roots.has(p)) roots.add(p);
  };
  add(envPath('ProgramFiles'));
  add(envPath('ProgramW6432'));
  add(envPath('ProgramFiles(x86)'));
  const local = envPath('LOCALAPPDATA');
  if (local) add(path.join(local, 'Programs'));
  // 其他盘符的 Program Files
  for (const letter of 'CDEFGHIJ') {
    add(`${letter}:\\Program Files`);
    add(`${letter}:\\Program Files (x86)`);
    add(`${letter}:\\Programs`);
  }
  return [...roots];
}

async function exists(p) {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}

/** 读取 asar 内的 package.json（只解析头部，不整包解压） */
async function readAsarPackageVersion(asarPath) {
  let fh;
  try {
    fh = await fs.open(asarPath, 'r');
    const head = Buffer.alloc(16);
    await fh.read(head, 0, 16, 0);
    const headerSize = head.readUInt32LE(12);
    if (!headerSize || headerSize > 64 * 1024 * 1024) return null;
    const headerBuf = Buffer.alloc(headerSize);
    await fh.read(headerBuf, 0, headerSize, 16);
    const header = JSON.parse(headerBuf.toString('utf8'));
    const entry = header?.files?.['package.json'];
    if (!entry || entry.files) return null;
    const size = Number(entry.size);
    const offset = Number(entry.offset);
    if (!Number.isFinite(size) || !Number.isFinite(offset)) return null;
    const data = Buffer.alloc(size);
    await fh.read(data, 0, size, 16 + headerSize + offset);
    const pkg = JSON.parse(data.toString('utf8'));
    return pkg.version || null;
  } catch {
    return null;
  } finally {
    await fh?.close().catch(() => {});
  }
}

async function readInstallVersion(dir) {
  const manifestPath = path.join(dir, 'resources', 'install-manifest.json');
  try {
    const raw = await fs.readFile(manifestPath, 'utf8');
    const manifest = JSON.parse(raw);
    if (manifest.appVersion) return { version: String(manifest.appVersion), source: 'install-manifest.json', generatedAt: manifest.generatedAt || '' };
  } catch {}
  const asarVersion = await readAsarPackageVersion(path.join(dir, 'resources', 'app.asar'));
  if (asarVersion) return { version: String(asarVersion), source: 'app.asar/package.json', generatedAt: '' };
  return null;
}

async function dirInfo(dir, app) {
  const exePath = path.join(dir, app.exe);
  if (!(await exists(exePath))) return null;
  const info = await readInstallVersion(dir);
  let asarSize = 0;
  try {
    const st = await fs.stat(path.join(dir, 'resources', 'app.asar'));
    asarSize = st.size;
  } catch {}
  return {
    appId: app.id,
    installDir: dir,
    exePath,
    version: info?.version || '',
    versionSource: info?.source || '',
    builtAt: info?.generatedAt || '',
    asarSize,
    found: true,
  };
}

const runningCache = new Map();

export async function isRunning(app) {
  const now = Date.now();
  const cached = runningCache.get(app.id);
  if (cached && now - cached.at < 4000) return cached.value;
  let value = false;
  try {
    const { stdout } = await execFileAsync('tasklist', ['/FI', `IMAGENAME eq ${app.process}`, '/NH'], {
      timeout: 4000,
      windowsHide: true,
    });
    value = stdout.toLowerCase().includes(app.process.toLowerCase());
  } catch {
    value = false;
  }
  runningCache.set(app.id, { at: now, value });
  return value;
}

export async function detectApps(customPaths = {}) {
  const roots = scanRoots();
  const results = {};
  for (const app of APPS) {
    const candidates = [];
    if (customPaths[app.id]) candidates.push(customPaths[app.id]);
    for (const root of roots) {
      candidates.push(path.join(root, app.name));
      candidates.push(path.join(root, app.name.replace(/\s+/g, '')));
      if (app.id === 'workbuddyai') {
        candidates.push(path.join(root, 'WorkBuddyAI'));
        candidates.push(path.join(root, 'WorkBuddy AI'));
      }
    }
    let found = null;
    for (const dir of [...new Set(candidates)]) {
      found = await dirInfo(dir, app);
      if (found) break;
    }
    results[app.id] = found || {
      appId: app.id,
      installDir: '',
      exePath: '',
      version: '',
      versionSource: '',
      builtAt: '',
      asarSize: 0,
      found: false,
    };
  }
  return results;
}

export async function detectOne(appId, customPaths = {}) {
  const all = await detectApps(customPaths);
  return all[appId];
}
