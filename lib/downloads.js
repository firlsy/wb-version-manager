import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { STORE_DIR, formatBytes } from './store.js';

const active = new Map(); // id -> job

/** 只允许从官方两条通道下载，防止被塞进来一个来路不明的 exe */
const ALLOWED_HOSTS = new Set([
  'download.codebuddy.cn',
  'download.codebuddy.ai',
  'acc-1258344699.cos.ap-guangzhou.myqcloud.com',
]);

function isAllowedPackageUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === 'https:' && ALLOWED_HOSTS.has(u.host.toLowerCase());
  } catch {
    return false;
  }
}

export function filenameFromUrl(url) {
  try {
    const u = new URL(url);
    // 先解码再取文件名：解码后的 %2e%2e%2f 会变成 ../，必须在这里掐掉
    const decoded = decodeURIComponent(u.pathname);
    const base = path.basename(decoded.replace(/\\/g, '/'));
    const safe = base.replace(/[<>:"|?*\u0000-\u001f]/g, '_');
    return safe || `package-${Date.now()}.exe`;
  } catch {
    return `package-${Date.now()}.exe`;
  }
}

/**
 * 从文件名里取版本号。3 段和 4 段都要认：
 * 官方包名带构建号（5.11.3.43806934），回收包可能是短版本（5.11.4-recycled），
 * 只认 4 段会把后者当成没有版本号的垃圾。
 */
export function parseVersionFromFilename(name) {
  const m = String(name || '').match(/(\d+(?:\.\d+){2,3})/);
  return m ? m[1] : '';
}

function platformFromFilename(name) {  if (/darwin-arm64/i.test(name)) return 'darwin-arm64';
  if (/darwin-x64/i.test(name)) return 'darwin-x64';
  if (/win32-x64-user/i.test(name)) return 'win32-x64-user';
  return '';
}

export function destFor(appId, url) {
  const name = filenameFromUrl(url);
  return { name, dir: path.join(STORE_DIR, appId), file: path.join(STORE_DIR, appId, name) };
}

/**
 * 用官方链接的文件名建一张「这个包属于哪个客户端」的索引。
 * 同一个文件名在两条通道都出现时记成 ambiguous，宁可放过也不误判。
 */
export function buildNameIndex(catalog) {
  const index = new Map();
  const add = (map, appId) => {
    for (const pkg of Object.values(map || {})) {
      if (!pkg?.url) continue;
      const name = filenameFromUrl(pkg.url).toLowerCase();
      if (!name) continue;
      const known = index.get(name);
      if (known && known !== appId) index.set(name, 'ambiguous');
      else index.set(name, appId);
    }
  };
  for (const rel of catalog?.releases || []) {
    add(rel.packages, 'workbuddy');
    add(rel.intlPackages, 'workbuddyai');
  }
  return index;
}

/** 返回该文件真正归属的 appId；对得上、索引缺失或归属不明时返回空串 */
function wrongEditionOf(fileName, appId, index) {
  if (!index) return '';
  const owner = index.get(String(fileName).toLowerCase());
  if (!owner || owner === 'ambiguous' || owner === appId) return '';
  return owner;
}

export async function listCached(index = null) {
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
      const wrong = wrongEditionOf(name, app.name, index);
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
        wrongEdition: wrong,
        usable: !wrong,
      });
    }
  }
  return out.sort((a, b) => b.mtime.localeCompare(a.mtime));
}

export async function findCached(appId, version, index = null) {
  const list = await listCached(index);
  return (
    list.find(
      (f) =>
        f.appId === appId &&
        f.platform === 'win32-x64-user' &&
        f.usable &&
        (f.versionShort === version || f.version === version || f.version?.startsWith(`${version}.`))
    ) || null
  );
}

function request(url, signal, offset = 0, ifRange = '') {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('https:') ? https : http;
    const headers = {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkBuddy-Version-Locker/1.2',
      Accept: '*/*',
    };
    if (offset > 0) headers.Range = `bytes=${offset}-`;
    // 文件换了服务器会回 200 整包，runJob 里看到非 206 就从 0 重写
    if (offset > 0 && ifRange) headers['If-Range'] = ifRange;
    const req = lib.get(url, { headers, signal }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        res.resume();
        const next = new URL(res.headers.location, url).toString();
        resolve(request(next, signal, offset));
        return;
      }
      if (res.statusCode !== 200 && res.statusCode !== 206 && res.statusCode !== 416) {
        res.resume();
        reject(new Error(`下载失败：HTTP ${res.statusCode}`));
        return;
      }
      resolve(res);
    });
    req.on('error', reject);
    req.setTimeout(30000, () => req.destroy(new Error('连接超时')));
  });
}

/* ---------------- 碎片指纹 ---------------- */
const metaPathOf = (part) => `${part}.meta`;

async function readMeta(part) {
  try {
    const raw = await fsp.readFile(metaPathOf(part), 'utf8');
    const j = JSON.parse(raw);
    return { url: String(j.url || ''), total: Number(j.total || 0), etag: String(j.etag || '') };
  } catch {
    return { url: '', total: 0, etag: '' };
  }
}

async function writeMeta(part, url, total, etag) {
  await fsp
    .writeFile(metaPathOf(part), JSON.stringify({ url, total: Number(total) || 0, etag: etag || '' }), 'utf8')
    .catch(() => {});
}

/** ETag 优先，退而求其次 Last-Modified：续传时作为 If-Range */
function validatorOf(res) {
  return String(res.headers.etag || res.headers['last-modified'] || '');
}

/** 删碎片一定连指纹一起删，否则下次续传会被指纹拦下来重下 */
async function removePart(part) {
  await fsp.unlink(part).catch(() => {});
  await fsp.unlink(metaPathOf(part)).catch(() => {});
}

/* ---------------- 下载队列 ---------------- */
const queue = [];
let workerRunning = false;

function jobPublic(j) {
  return {
    id: j.id,
    appId: j.appId,
    version: j.version,
    filename: j.filename,
    status: j.status,
    received: j.received,
    total: j.total,
    percent: j.total ? Math.min(100, Math.round((j.received / j.total) * 100)) : 0,
    speed: j.speed,
    speedText: `${formatBytes(j.speed)}/s`,
    error: j.error || '',
    startedAt: j.startedAt,
  };
}

export function listJobs() {
  return queue.map(jobPublic);
}

export function cancelJob(id) {
  return stopJob(id);
}

/** 暂停：保留 .part，继续时用 Range 续传 */
export function pauseJob(id) {
  const job = queue.find((j) => j.id === id);
  if (!job) return false;
  if (job.status === 'queued') { job.status = 'paused'; return true; }
  if (job.status !== 'downloading') return false;
  job.status = 'paused';
  if (job.controller) job.controller.abort();
  return true;
}

export function resumeJob(id) {
  const job = queue.find((j) => j.id === id);
  if (!job) return false;
  if (!['paused', 'stopped', 'error'].includes(job.status)) return false;
  job.status = 'queued';
  job.error = '';
  ensureWorker();
  return true;
}

/** 停止：中断并删掉碎片，任务留在列表里可以重下 */
export async function stopJob(id) {
  const job = queue.find((j) => j.id === id);
  if (!job) return false;
  if (job.status === 'downloading') {
    job.status = 'stopped';
    if (job.controller) job.controller.abort();
    return true;
  }
  job.status = 'stopped';
  job.received = 0;
  job.total = 0;
  job.speed = 0;
  await removePart(job.part);
  return true;
}

/** 删除：从列表移除；未完成的连碎片一起删，已完成的只删记录 */
export async function removeJob(id) {
  const index = queue.findIndex((j) => j.id === id);
  if (index < 0) return false;
  const job = queue[index];
  job.deleted = true;
  if (job.status === 'downloading' && job.controller) job.controller.abort();
  queue.splice(index, 1);
  if (job.status !== 'done') await removePart(job.part);
  return true;
}

/**
 * 不在任何队列任务里的 .part：上次异常退出、失败后没再续传留下的。
 * 连 .meta 指纹一起清，并把「碎片没了但指纹还在」的孤儿 meta 也扫掉。
 */
export async function clearOrphanParts() {
  const keep = new Set(queue.filter((j) => j.status !== 'done').map((j) => j.part));
  let count = 0;
  let bytes = 0;
  let apps = [];
  try {
    apps = await fsp.readdir(STORE_DIR, { withFileTypes: true });
  } catch {
    return { count, bytes, sizeText: formatBytes(0) };
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
    const existing = new Set(files);
    for (const name of files) {
      const full = path.join(dir, name);
      if (name.endsWith('.meta')) {
        // 指纹对应的碎片已经没了（手动删过、或旧版本留下的），留着没用
        if (!existing.has(name.slice(0, -'.meta'.length))) await fsp.unlink(full).catch(() => {});
        continue;
      }
      if (!name.endsWith('.part')) continue;
      if (keep.has(full)) continue;
      const st = await fsp.stat(full).catch(() => null);
      await removePart(full);
      count++;
      if (st) bytes += st.size;
    }
  }
  return { count, bytes, sizeText: formatBytes(bytes) };
}

export async function clearFinishedJobs() {
  const keep = [];
  let n = 0;
  for (const job of queue) {
    if (!['done', 'error', 'stopped'].includes(job.status)) { keep.push(job); continue; }
    job.deleted = true;
    if (job.status !== 'done') await removePart(job.part);
    n++;
  }
  queue.length = 0;
  queue.push(...keep);
  return n;
}

function ensureWorker() {
  if (workerRunning) return;
  workerRunning = true;
  (async () => {
    try {
      for (;;) {
        const job = queue.find((j) => j.status === 'queued' && !j.deleted);
        if (!job) break;
        await runJob(job);
      }
    } finally {
      workerRunning = false;
      if (queue.some((j) => j.status === 'queued' && !j.deleted)) ensureWorker();
    }
  })();
}

async function runJob(job) {
  job.status = 'downloading';
  job.error = '';
  job.controller = new AbortController();
  const signal = job.controller.signal;

  const meta = await readMeta(job.part);
  let offset = 0;
  try { offset = (await fsp.stat(job.part)).size; } catch { offset = 0; }
  // 没有指纹（旧版本留下的碎片）或地址变了：来路不明，不能接在后面下
  if (offset > 0 && meta.url !== job.url) {
    await removePart(job.part);
    offset = 0;
  }
  job.received = offset;

  try {
    for (let attempt = 0; attempt < 3; attempt++) {
      const res = await request(job.url, signal, offset, meta.etag);
      if (offset > 0 && res.statusCode === 416) {
        res.resume();
        await removePart(job.part);
        offset = 0;
        job.received = 0;
        continue;
      }

      const resumed = res.statusCode === 206;
      const start = resumed ? offset : 0;
      if (!resumed && offset > 0) await removePart(job.part);

      const len = Number(res.headers['content-length'] || 0);
      let total = len > 0 ? start + len : 0;
      if (resumed) {
        const rangeTotal = Number((/\/(\d+)$/.exec(String(res.headers['content-range'] || '')) || [])[1] || 0);
        if (rangeTotal && meta.total && rangeTotal !== meta.total) {
          // 文件换了（CDN 重新打包）：旧碎片接上去只会拼出坏包
          res.destroy();
          await removePart(job.part);
          offset = 0;
          job.received = 0;
          meta.total = 0;
          continue;
        }
        if (rangeTotal) total = rangeTotal;
      }
      job.total = total;
      job.received = start;
      await writeMeta(job.part, job.url, total, validatorOf(res));

      const out = fs.createWriteStream(job.part, { flags: start > 0 ? 'a' : 'w' });
      let lastTick = Date.now();
      let lastBytes = start;
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

      if (job.deleted) {
        // 收尾瞬间被删除：别把文件又落回磁盘
        await removePart(job.part);
        return;
      }
      await fsp.unlink(job.dest).catch(() => {});
      await fsp.rename(job.part, job.dest);
      await fsp.unlink(metaPathOf(job.part)).catch(() => {});
      job.status = 'done';
      job.speed = 0;
      if (!job.total) job.total = job.received;
      return;
    }
    throw new Error('服务器上的安装包反复变化，下载已放弃');
  } catch (err) {
    job.speed = 0;
    if (job.deleted || job.status === 'stopped') {
      job.status = 'stopped';
      job.received = 0;
      job.total = 0;
      await removePart(job.part);
    } else if (job.status === 'paused') {
      // 暂停：半截文件留着，继续时接着下
    } else {
      job.status = 'error';
      job.error = err.message || String(err);
    }
  } finally {
    job.controller = null;
  }
}

export async function startDownload({ appId, version, url }) {
  if (!url) throw new Error('该版本没有可用的 Windows 安装包链接');
  if (!isAllowedPackageUrl(url)) throw new Error('不是官方安装包地址，已拒绝下载');
  const id = `${appId}:${version}:win32-x64-user`;

  const existing = queue.find((j) => j.id === id);
  if (existing) {
    if (['paused', 'error', 'stopped'].includes(existing.status)) {
      existing.status = 'queued';
      existing.error = '';
      ensureWorker();
    }
    return jobPublic(existing);
  }

  const { name, dir, file } = destFor(appId, url);
  await fsp.mkdir(dir, { recursive: true });
  const job = {
    id,
    appId,
    version,
    url,
    filename: name,
    dest: file,
    part: `${file}.part`,
    received: 0,
    total: 0,
    speed: 0,
    status: 'queued',
    error: '',
    startedAt: new Date().toISOString(),
    controller: null,
    deleted: false,
  };
  queue.push(job);
  ensureWorker();
  return jobPublic(job);
}
