import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { STORE_DIR, formatBytes } from './store.js';

const active = new Map(); // id -> job

function filenameFromUrl(url) {
  try {
    const u = new URL(url);
    return decodeURIComponent(path.basename(u.pathname));
  } catch {
    return `package-${Date.now()}.exe`;
  }
}

function parseVersionFromFilename(name) {
  const m = name.match(/(\d+\.\d+\.\d+\.\d+)/);
  return m ? m[1] : '';
}

function platformFromFilename(name) {
  if (/darwin-arm64/i.test(name)) return 'darwin-arm64';
  if (/darwin-x64/i.test(name)) return 'darwin-x64';
  if (/win32-x64-user/i.test(name)) return 'win32-x64-user';
  return '';
}

export function destFor(appId, url) {
  const name = filenameFromUrl(url);
  return { name, dir: path.join(STORE_DIR, appId), file: path.join(STORE_DIR, appId, name) };
}

export async function listCached() {
  const out = [];
  let apps = [];
  try {
    apps = await fsp.readdir(STORE_DIR, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const app of apps) {
    if (!app.isDirectory()) continue;
    const dir = path.join(STORE_DIR, app.name);
    let files = [];
    try {
      files = await fsp.readdir(dir);
    } catch {
      continue;
    }
    for (const name of files) {
      if (!/\.(exe|dmg|zip|AppImage)$/i.test(name)) continue;
      const full = path.join(dir, name);
      const st = await fsp.stat(full).catch(() => null);
      if (!st) continue;
      out.push({
        appId: app.name,
        filename: name,
        path: full,
        size: st.size,
        sizeText: formatBytes(st.size),
        mtime: st.mtime.toISOString(),
        version: parseVersionFromFilename(name),
        versionShort: parseVersionFromFilename(name).split('.').slice(0, 3).join('.'),
        build: (name.match(/(\d+\.\d+\.\d+\.\d+)/) || [])[1] || '',
        platform: platformFromFilename(name),
      });
    }
  }
  return out.sort((a, b) => b.mtime.localeCompare(a.mtime));
}

export async function findCached(appId, version) {
  const list = await listCached();
  return (
    list.find(
      (f) =>
        f.appId === appId &&
        f.platform === 'win32-x64-user' &&
        (f.versionShort === version || f.version === version || f.version?.startsWith(`${version}.`))
    ) || null
  );
}

function request(url, signal) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https:') ? https : http;
    const req = lib.get(
      url,
      {
        headers: {
          'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkBuddy-Version-Locker/1.0',
          Accept: '*/*',
        },
        signal,
      },
      (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          const next = new URL(res.headers.location, url).toString();
          resolve(request(next, signal));
          return;
        }
        if (res.statusCode !== 200) {
          res.resume();
          reject(new Error(`下载失败：HTTP ${res.statusCode}`));
          return;
        }
        resolve(res);
      }
    );
    req.on('error', reject);
    req.setTimeout(30000, () => req.destroy(new Error('连接超时')));
  });
}

export function getJob(id) {
  return active.get(id) || null;
}

export function listJobs() {
  return [...active.values()].map((j) => ({
    id: j.id,
    appId: j.appId,
    version: j.version,
    filename: j.filename,
    received: j.received,
    total: j.total,
    percent: j.total ? Math.min(100, Math.round((j.received / j.total) * 100)) : 0,
    speed: j.speed,
    speedText: `${formatBytes(j.speed)}/s`,
    status: j.status,
    error: j.error || '',
    startedAt: j.startedAt,
  }));
}

export function cancelJob(id) {
  const job = active.get(id);
  if (!job) return false;
  job.cancelled = true;
  job.controller.abort();
  return true;
}

export async function startDownload({ appId, version, url }) {
  if (!url) throw new Error('该版本没有可用的 Windows 安装包链接');
  const id = `${appId}:${version}:win32-x64-user`;
  const existing = active.get(id);
  if (existing && existing.status === 'downloading') return existing.public();

  const { name, dir, file } = destFor(appId, url);
  await fsp.mkdir(dir, { recursive: true });

  const controller = new AbortController();
  const job = {
    id,
    appId,
    version,
    filename: name,
    dest: file,
    received: 0,
    total: 0,
    speed: 0,
    status: 'downloading',
    error: '',
    startedAt: new Date().toISOString(),
    controller,
    cancelled: false,
    public() {
      return {
        id: this.id,
        appId: this.appId,
        version: this.version,
        filename: this.filename,
        status: this.status,
        received: this.received,
        total: this.total,
        percent: this.total ? Math.min(100, Math.round((this.received / this.total) * 100)) : 0,
      };
    },
  };
  active.set(id, job);

  (async () => {
    const part = `${file}.part`;
    try {
      const res = await request(url, controller.signal);
      job.total = Number(res.headers['content-length'] || 0);
      const out = fs.createWriteStream(part);
      let lastTick = Date.now();
      let lastBytes = 0;
      res.on('data', (chunk) => {
        job.received += chunk.length;
        const now = Date.now();
        if (now - lastTick >= 500) {
          job.speed = Math.round(((job.received - lastBytes) * 1000) / (now - lastTick));
          lastTick = now;
          lastBytes = job.received;
        }
      });
      await new Promise((resolve, reject) => {
        res.pipe(out);
        res.on('end', resolve);
        res.on('error', reject);
        out.on('error', reject);
      });
      await new Promise((r) => out.close(r));
      if (job.cancelled) throw new Error('已取消');
      await fsp.rename(part, file);
      job.status = 'done';
      job.speed = 0;
      job.percent = 100;
    } catch (err) {
      job.status = job.cancelled ? 'cancelled' : 'error';
      job.error = err.message || String(err);
      job.speed = 0;
      await fsp.unlink(part).catch(() => {});
    } finally {
      setTimeout(() => active.delete(id), 15000);
    }
  })();

  return job.public();
}
