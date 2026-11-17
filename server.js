import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { ROOT, EXPORT_DIR, ensureDirs, loadState, saveState, formatBytes, seriesOf, cmpVersion } from './lib/store.js';
import { APPS, appById, detectApps, isRunning } from './lib/detect.js';
import { loadCatalog, catalogMeta, refreshCatalog, mergeLocalVersions } from './lib/catalog.js';
import { listCached, startDownload, listJobs, cancelJob, findCached } from './lib/downloads.js';
import { installVersion, killApp, dataSnapshot } from './lib/install.js';
import { setLock, clearLock, guardStatus, setGuard, runGuardNow } from './lib/lock.js';
import { blockStatus, enableBlock, disableBlock } from './lib/block.js';
import { PLATFORM_LABELS, countNotes } from './lib/parse.js';

const PUBLIC_DIR = path.join(ROOT, 'public');
const PORT_START = Number(process.env.PORT || 17321);
const HOST = '127.0.0.1';
const NO_OPEN = process.argv.includes('--no-open');

const refreshState = { running: false, message: '', percent: 0, result: null, error: '' };

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

function json(res, code, data) {
  const body = JSON.stringify(data);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
}

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  if (!chunks.length) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    return {};
  }
}

async function serveStatic(req, res, pathname) {
  const rel = pathname === '/' ? '/index.html' : pathname;
  const file = path.join(PUBLIC_DIR, path.normalize(rel).replace(/^([/\\])+/, ''));
  if (!file.startsWith(PUBLIC_DIR)) {
    res.writeHead(403);
    res.end('Forbidden');
    return;
  }
  try {
    const data = await fs.readFile(file);
    res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' });
    res.end(data);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('Not found');
  }
}

async function buildState() {
  const state = await loadState();
  const catalog = await loadCatalog();
  const detected = await detectApps(state.settings.customPaths);
  const jobs = listJobs();
  const cached = await listCached();
  const latest = catalog.latest;

  const apps = [];
  for (const app of APPS) {
    const info = detected[app.id];
    const lock = state.locks[app.id] || null;
    const local = [info.version].filter(Boolean);
    const releases = mergeLocalVersions(catalog.releases, local);
    apps.push({
      ...app,
      install: info,
      running: info.found ? await isRunning(app) : false,
      lock,
      drift: Boolean(lock && info.version && lock.version !== info.version),
      latest: latest ? { version: latest.version, build: latest.build, publishedAt: latest.publishedAt } : null,
      upToDate: Boolean(latest && info.version && cmpVersion(info.version, latest.version) >= 0),
      releaseCount: releases.length,
      hasUpdate: Boolean(latest && info.version && cmpVersion(info.version, latest.version) < 0),
    });
  }

  return {
    apps,
    catalog: catalogMeta(),
    jobs,
    cached,
    settings: state.settings,
    guard: await guardStatus(),
    block: await blockStatus(),
    lastInstall: state.lastInstall,
    guardEvents: (state.guardEvents || []).slice(-5),
    dataDirs: await dataSnapshot(),
    refresh: refreshState,
    storeDir: path.join(ROOT, 'store'),
    now: new Date().toISOString(),
  };
}

function exportText(format, items) {
  if (format === 'csv') {
    const rows = [['应用', '版本', '平台', '发布日期', '大小', '下载链接']];
    for (const it of items) {
      rows.push([it.appName, it.version, PLATFORM_LABELS[it.platform] || it.platform, it.date || '', it.sizeText || '', it.url]);
    }
    return '\uFEFF' + rows.map((r) => r.map((c) => (/[",\n]/.test(String(c)) ? `"${String(c).replace(/"/g, '""')}"` : c)).join(',')).join('\r\n');
  }
  if (format === 'markdown') {
    return items.map((it) => `- **${it.appName} ${it.version}** · ${PLATFORM_LABELS[it.platform] || it.platform} · ${it.date || '日期未知'}\n  ${it.url}`).join('\n');
  }
  if (format === 'json') {
    return JSON.stringify(items, null, 2);
  }
  return items.map((it) => it.url).join('\r\n');
}

async function handler(req, res) {
  const url = new URL(req.url, `http://${HOST}`);
  const p = url.pathname;

  try {
    if (p === '/api/state' && req.method === 'GET') {
      return json(res, 200, await buildState());
    }

    if (p === '/api/versions' && req.method === 'GET') {
      const appId = url.searchParams.get('appId') || 'workbuddy';
      const state = await loadState();
      const catalog = await loadCatalog();
      const info = (await detectApps(state.settings.customPaths))[appId];
      const releases = mergeLocalVersions(catalog.releases, [info?.version].filter(Boolean));
      const lock = state.locks[appId] || null;
      const cached = await listCached();
      // 国内版与国际版是两套安装包，按当前客户端选对应的那一套，绝不跨版回退
      const latestForApp = appId === 'workbuddyai' ? catalog.latestIntl : catalog.latest;
      const items = releases.map((r) => {
        const pkgs = (appId === 'workbuddyai' ? r.intlPackages : r.packages) || {};
        const win = pkgs['win32-x64-user'];
        const cache = cached.find((c) => c.appId === appId && (c.versionShort === r.version || c.version === r.version) && c.platform === 'win32-x64-user');
        return {
          ...r,
          packages: pkgs,
          intlPackages: r.intlPackages || {},
          series: seriesOf(r.version),
          noteCount: countNotes(r.notes || []),
          platformLabels: PLATFORM_LABELS,
          winUrl: win?.url || '',
          winSize: win?.sizeText || '',
          installed: info?.version === r.version,
          locked: lock?.version === r.version,
          latest: latestForApp?.version === r.version,
          downloaded: Boolean(cache),
          cachedSize: cache?.sizeText || '',
        };
      });
      return json(res, 200, {
        appId,
        installedVersion: info?.version || '',
        lock,
        latest: latestForApp,
        generatedAt: catalog.generatedAt,
        releases: items,
      });
    }

    if (p === '/api/refresh' && req.method === 'POST') {
      if (refreshState.running) return json(res, 200, { started: false, running: true });
      refreshState.running = true;
      refreshState.message = '准备中…';
      refreshState.percent = 0;
      refreshState.result = null;
      refreshState.error = '';
      refreshCatalog((message, percent) => {
        refreshState.message = message;
        refreshState.percent = percent ?? refreshState.percent;
      })
        .then((result) => {
          refreshState.result = result;
        })
        .catch((err) => {
          refreshState.error = err.message || String(err);
        })
        .finally(() => {
          refreshState.running = false;
          refreshState.percent = 100;
        });
      return json(res, 200, { started: true, running: true });
    }

    if (p === '/api/refresh/status' && req.method === 'GET') {
      return json(res, 200, refreshState);
    }

    if (p === '/api/lock' && req.method === 'POST') {
      const body = await readBody(req);
      if (!appById(body.appId)) return json(res, 400, { error: '未知应用' });
      if (!body.version) return json(res, 400, { error: '缺少版本号' });
      const lock = await setLock(body.appId, body.version, {
        build: body.build || '',
        note: body.note || '',
        source: 'user',
      });
      let block = null;
      if (body.blockUpdates !== false) {
        try {
          block = await enableBlock(body.appId);
        } catch (err) {
          block = { error: err.message };
        }
      }
      return json(res, 200, { ok: true, lock, block });
    }

    if (p === '/api/block' && req.method === 'POST') {
      const body = await readBody(req);
      if (!appById(body.appId)) return json(res, 400, { error: '未知应用' });
      try {
        const result = body.enabled ? await enableBlock(body.appId) : await disableBlock(body.appId);
        return json(res, 200, { ok: true, ...result });
      } catch (err) {
        return json(res, 400, { error: err.message });
      }
    }

    if (p.startsWith('/api/lock/') && req.method === 'DELETE') {
      const appId = p.split('/').pop();
      await clearLock(appId);
      let block = null;
      try {
        block = await disableBlock(appId);
      } catch (err) {
        block = { error: err.message };
      }
      return json(res, 200, { ok: true, block });
    }

    if (p === '/api/download' && req.method === 'POST') {
      const body = await readBody(req);
      if (!appById(body.appId)) return json(res, 400, { error: '未知应用' });
      const job = await startDownload({ appId: body.appId, version: body.version, url: body.url });
      return json(res, 200, { ok: true, job });
    }

    if (p === '/api/downloads' && req.method === 'GET') {
      return json(res, 200, { jobs: listJobs(), cached: await listCached() });
    }

    if (p.startsWith('/api/download/') && p.endsWith('/cancel') && req.method === 'POST') {
      const id = decodeURIComponent(p.split('/')[3] || '');
      return json(res, 200, { ok: cancelJob(id) });
    }

    if (p === '/api/install' && req.method === 'POST') {
      const body = await readBody(req);
      if (!appById(body.appId)) return json(res, 400, { error: '未知应用' });
      let cached = await findCached(body.appId, body.version);
      if (!cached && body.path) {
        const st = await fs.stat(body.path).catch(() => null);
        if (st) cached = { path: body.path };
      }
      if (!cached) return json(res, 400, { error: '本地没有该版本的安装包，请先下载' });
      const catalog = await loadCatalog();
      const latestForApp = body.appId === 'workbuddyai' ? catalog.latestIntl : catalog.latest;
      const sha256 = latestForApp?.version === body.version ? (latestForApp.sha256 || '') : '';
      const result = await installVersion({
        appId: body.appId,
        version: body.version,
        installerPath: cached.path,
        sha256: body.verify === false ? '' : sha256,
      });
      return json(res, 200, { ok: result.ok, result });
    }

    if (p === '/api/kill' && req.method === 'POST') {
      const body = await readBody(req);
      const result = await killApp(body.appId);
      return json(res, 200, { ok: true, result });
    }

    if (p === '/api/settings' && req.method === 'POST') {
      const body = await readBody(req);
      const state = await loadState();
      const settings = { ...state.settings, ...(body.settings || {}) };
      await saveState({ settings });
      let guardResult = null;
      if (typeof body.guard === 'boolean') {
        guardResult = await setGuard(body.guard, {
          nodePath: process.execPath,
          guardScript: path.join(ROOT, 'guard.js'),
        });
      }
      return json(res, 200, { ok: true, settings, guard: guardResult, guardStatus: await guardStatus() });
    }

    if (p === '/api/guard/run' && req.method === 'POST') {
      const result = await runGuardNow({ nodePath: process.execPath, guardScript: path.join(ROOT, 'guard.js') });
      return json(res, 200, { ok: result.code === 0, result });
    }

    if (p === '/api/export' && req.method === 'POST') {
      const body = await readBody(req);
      const items = Array.isArray(body.items) ? body.items : [];
      const format = body.format || 'txt';
      const text = exportText(format, items);
      const ext = format === 'markdown' ? 'md' : format === 'json' ? 'json' : format;
      const filename = body.filename || `wb-packages-${Date.now()}.${ext}`;
      await fs.mkdir(EXPORT_DIR, { recursive: true });
      const file = path.join(EXPORT_DIR, filename);
      await fs.writeFile(file, text, 'utf8');
      return json(res, 200, { ok: true, path: file, filename, text, count: items.length });
    }

    if (p === '/api/reveal' && req.method === 'POST') {
      const body = await readBody(req);
      const target = body.path ? path.resolve(body.path) : ROOT;
      const allowed = target.startsWith(ROOT) || target.startsWith(process.env.ProgramFiles || '\0') || /WorkBuddy/i.test(target);
      if (!allowed) return json(res, 400, { error: '路径不在允许范围' });
      const st = await fs.stat(target).catch(() => null);
      if (!st) return json(res, 404, { error: '路径不存在' });
      spawn('explorer.exe', [st.isDirectory() ? target : `/select,${target}`], { detached: true, stdio: 'ignore' }).unref();
      return json(res, 200, { ok: true });
    }

    if (p === '/api/catalog' && req.method === 'GET') {
      const catalog = await loadCatalog();
      return json(res, 200, { ...catalogMeta(), releases: catalog.releases.length });
    }

    if (p.startsWith('/api/')) {
      return json(res, 404, { error: '接口不存在' });
    }

    return await serveStatic(req, res, p);
  } catch (err) {
    console.error('[api]', p, err);
    return json(res, 500, { error: err.message || String(err) });
  }
}

async function listen(port) {
  return new Promise((resolve, reject) => {
    const server = http.createServer(handler);
    server.once('error', (err) => {
      if (err.code === 'EADDRINUSE') resolve(null);
      else reject(err);
    });
    server.listen(port, HOST, () => resolve(server));
  });
}

async function main() {
  await ensureDirs();
  await loadCatalog();
  let port = PORT_START;
  let server = null;
  for (let i = 0; i < 12; i++) {
    server = await listen(port + i);
    if (server) {
      port = port + i;
      break;
    }
  }
  if (!server) throw new Error('端口被占用，无法启动');
  const url = `http://${HOST}:${port}`;
  console.log(`\n  WB 版本管理器 已启动\n  ${url}\n  按 Ctrl+C 停止\n`);
  if (!NO_OPEN) {
    spawn('cmd', ['/c', 'start', '', url], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
