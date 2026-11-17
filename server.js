import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { ROOT, DATA_DIR, STORE_DIR, EXPORT_DIR, ensureDirs, loadState, saveState, formatBytes, seriesOf, cmpVersion, sameVersion } from './lib/store.js';
import { APPS, appById, detectApps, isRunning } from './lib/detect.js';
import { loadCatalog, catalogMeta, refreshCatalog, mergeLocalVersions } from './lib/catalog.js';
import { listCached, startDownload, listJobs, cancelJob, pauseJob, resumeJob, stopJob, removeJob, clearFinishedJobs, clearOrphanParts, findCached, buildNameIndex } from './lib/downloads.js';
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

const MAX_BODY_BYTES = 2 * 1024 * 1024;

async function readBody(req) {
  const chunks = [];
  let size = 0;
  for await (const c of req) {
    size += c.length;
    if (size > MAX_BODY_BYTES) {
      const err = new Error('请求体过大');
      err.statusCode = 413;
      throw err;
    }
    chunks.push(c);
  }
  if (!chunks.length) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    return {};
  }
}

/** 只允许展示这些目录下的路径；不再用 /WorkBuddy/ 这种宽松匹配 */
function revealBases() {
  return [
    ROOT,
    STORE_DIR,
    EXPORT_DIR,
    DATA_DIR,
    process.env.ProgramFiles,
    process.env['ProgramFiles(x86)'],
    process.env.ProgramW6432,
    process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Programs') : '',
  ]
    .filter(Boolean)
    .map((item) => path.resolve(item).toLowerCase());
}

function canReveal(target) {
  const t = path.resolve(target).toLowerCase();
  return revealBases().some((base) => t === base || t.startsWith(base + path.sep));
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
  const nameIndex = nameIndexOf(catalog);
  const cached = await listCached(nameIndex);

  const apps = [];
  for (const app of APPS) {
    const info = detected[app.id];
    const lock = state.locks[app.id] || null;
    const local = [info.version].filter(Boolean);
    const releases = mergeLocalVersions(catalog.releases, local);
    // 两条通道各自只认自己的最新版，不能拿国内版的版本号去给国际版算「可升级」
    const latestForApp = app.id === 'workbuddyai' ? catalog.latestIntl : catalog.latest;
    apps.push({
      ...app,
      install: info,
      running: info.found ? await isRunning(app) : false,
      lock,
      drift: Boolean(lock && info.version && !sameVersion(lock.version, info.version)),
      latest: latestForApp
        ? { version: latestForApp.version, build: latestForApp.build, publishedAt: latestForApp.publishedAt }
        : null,
      upToDate: Boolean(latestForApp && info.version && cmpVersion(info.version, latestForApp.version) >= 0),
      releaseCount: releases.length,
      hasUpdate: Boolean(latestForApp && info.version && cmpVersion(info.version, latestForApp.version) < 0),
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

/* 目录里 (appId, version) 对应的官方安装包地址，找不到返回空 */
function catalogUrlFor(catalog, appId, version) {
  for (const rel of catalog.releases || []) {
    if (!sameVersion(rel.version, version)) continue;
    const map = appId === 'workbuddyai' ? rel.intlPackages : rel.packages;
    const pkg = map && map['win32-x64-user'];
    return pkg && pkg.url ? pkg.url : '';
  }
  return '';
}

/* 名字索引只跟目录内容有关：每次请求重建一遍纯属浪费（轮询 1.2s 一次） */
let _nameIndex = { key: '', value: null };
function nameIndexOf(catalog) {
  const key = `${catalog?.generatedAt || ''}|${catalog?.releases?.length || 0}`;
  if (_nameIndex.key !== key) _nameIndex = { key, value: buildNameIndex(catalog) };
  return _nameIndex.value;
}

/* ---------------- 工具自身的版本与更新检查 ---------------- */

const APP_REPO = 'https://github.com/firlsy/wb-version-manager';
const APP_RELEASES = `${APP_REPO}/releases/latest`;
const APP_LATEST_API = 'https://api.github.com/repos/firlsy/wb-version-manager/releases/latest';

// 版本号的唯一来源是 client/src/Core.cs 的 AppInfo.Version：
// exe 文件名、窗口标题、--version、网页页头都从这一处读，不存在第二份需要同步的版本字符串。
async function readAppVersion() {
  try {
    const cs = await fs.readFile(path.join(ROOT, 'client', 'src', 'Core.cs'), 'utf8');
    const m = cs.match(/public\s+const\s+string\s+Version\s*=\s*"([^"]+)"/);
    if (m) return m[1];
  } catch {}
  try {
    const pkg = JSON.parse(await fs.readFile(path.join(ROOT, 'package.json'), 'utf8'));
    if (pkg.version) return pkg.version;
  } catch {}
  return '0.0.0';
}

const APP_VERSION = await readAppVersion();

async function checkToolUpdate() {
  const base = { current: APP_VERSION, repoUrl: APP_REPO, releasesUrl: APP_RELEASES };
  try {
    const res2 = await fetch(APP_LATEST_API, {
      headers: {
        'User-Agent': 'wb-version-manager',
        Accept: 'application/vnd.github+json',
      },
      signal: AbortSignal.timeout(9000),
    });
    if (!res2.ok) throw new Error(`GitHub 返回 HTTP ${res2.status}`);
    const j = await res2.json();
    const latest = String(j.tag_name || j.name || '').replace(/^v/i, '').trim();
    if (!/^\d+\.\d+\.\d+$/.test(latest)) throw new Error('GitHub 返回的版本号无法识别');
    return {
      ...base,
      ok: true,
      latest,
      hasUpdate: cmpVersion(latest, APP_VERSION) > 0,
      name: j.name || `v${latest}`,
      url: j.html_url || APP_RELEASES,
      publishedAt: j.published_at || '',
      notes: typeof j.body === 'string' ? j.body.slice(0, 4000) : '',
    };
  } catch (err) {
    const detail = err?.cause?.message ? `${err.message}（${err.cause.message}）` : String(err.message || err);
    return { ...base, ok: false, error: detail };
  }
}

async function handler(req, res) {
  const url = new URL(req.url, `http://${HOST}`);
  const p = url.pathname;

  try {
    if (p === '/api/app' && req.method === 'GET') {
      return json(res, 200, {
        version: APP_VERSION,
        exeName: `WB-Version-Manager-universal-v${APP_VERSION}.exe`,
        title: `WB 版本管理器 v${APP_VERSION}`,
        repoUrl: APP_REPO,
        releasesUrl: APP_RELEASES,
      });
    }

    if (p === '/api/update-check' && req.method === 'GET') {
      return json(res, 200, await checkToolUpdate());
    }

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
      const cached = await listCached(nameIndexOf(catalog));
      // 国内版与国际版是两套安装包，按当前客户端选对应的那一套，绝不跨版回退
      const latestForApp = appId === 'workbuddyai' ? catalog.latestIntl : catalog.latest;
      const items = releases.map((r) => {
        const pkgs = (appId === 'workbuddyai' ? r.intlPackages : r.packages) || {};
        const win = pkgs['win32-x64-user'];
        const cache = cached.find(
          (c) =>
            c.appId === appId &&
            c.usable &&
            (c.versionShort === r.version || c.version === r.version) &&
            c.platform === 'win32-x64-user'
        );
        return {
          ...r,
          packages: pkgs,
          intlPackages: r.intlPackages || {},
          series: seriesOf(r.version),
          noteCount: countNotes(r.notes || []),
          platformLabels: PLATFORM_LABELS,
          winUrl: win?.url || '',
          winSize: win?.sizeText || '',
          recorded: Boolean(win?.recorded),
          recordedAt: win?.recordedAt || '',
          installed: sameVersion(info?.version, r.version),
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
      if (!body.version) return json(res, 400, { error: '缺少版本号' });

      // 只允许下「目录里这个版本对应的那个地址」：
      // 否则只要域名在白名单里，就能把任意官方包塞进这个客户端的草稿目录。
      const catalog = await loadCatalog();
      const expected = catalogUrlFor(catalog, body.appId, body.version);
      if (!expected) return json(res, 400, { error: '官方目录里没有这个版本的安装包' });
      if (String(body.url || '') !== expected) {
        return json(res, 400, { error: '安装包地址与版本不匹配，请刷新目录后重试' });
      }

      const job = await startDownload({ appId: body.appId, version: body.version, url: expected });
      return json(res, 200, { ok: true, job });
    }

    if (p === '/api/downloads' && req.method === 'GET') {
      const catalog = await loadCatalog();
      return json(res, 200, { jobs: listJobs(), cached: await listCached(nameIndexOf(catalog)) });
    }

    // 队列操作：暂停 / 继续 / 停止 / 删除（cancel 保留给老前端）
    const dlAction = /^\/api\/download\/(.+)\/(pause|resume|stop|cancel|remove)$/.exec(p);
    if (dlAction && req.method === 'POST') {
      const id = decodeURIComponent(dlAction[1]);
      const action = dlAction[2];
      let ok = false;
      if (action === 'pause') ok = pauseJob(id);
      else if (action === 'resume') ok = resumeJob(id);
      else if (action === 'remove') ok = await removeJob(id);
      else ok = await stopJob(id);
      return json(res, 200, { ok, jobs: listJobs() });
    }

    if (p === '/api/downloads/clear' && req.method === 'POST') {
      return json(res, 200, { ok: true, cleared: await clearFinishedJobs(), jobs: listJobs() });
    }

    if (p === '/api/downloads/parts/clear' && req.method === 'POST') {
      const r = await clearOrphanParts();
      return json(res, 200, { ok: true, ...r });
    }

    if (p === '/api/install' && req.method === 'POST') {
      const body = await readBody(req);
      if (!appById(body.appId)) return json(res, 400, { error: '未知应用' });
      if (!body.version) return json(res, 400, { error: '缺少版本号' });
      const catalog = await loadCatalog();
      // 只认缓存里、且归属正确的包。不接受客户端指定任意路径，
      // 否则可以把另一版的安装包塞进来静默安装。
      const cached = await findCached(body.appId, body.version, nameIndexOf(catalog));
      if (!cached) return json(res, 400, { error: '本地没有该版本可用的安装包，请先下载' });
      const latestForApp = body.appId === 'workbuddyai' ? catalog.latestIntl : catalog.latest;
      const sha256 = sameVersion(latestForApp?.version, body.version) ? latestForApp.sha256 || '' : '';
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
      // 只取文件名，别让 ../ 把导出写到 exports 外面
      const filename = path.basename(String(body.filename || `wb-packages-${Date.now()}.${ext}`));
      await fs.mkdir(EXPORT_DIR, { recursive: true });
      const file = path.join(EXPORT_DIR, filename);
      await fs.writeFile(file, text, 'utf8');
      return json(res, 200, { ok: true, path: file, filename, text, count: items.length });
    }

    if (p === '/api/reveal' && req.method === 'POST') {
      const body = await readBody(req);
      const target = body.path ? path.resolve(body.path) : ROOT;
      if (!canReveal(target)) return json(res, 400, { error: '路径不在允许范围' });
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
    if (!err.statusCode || err.statusCode >= 500) console.error('[api]', p, err);
    return json(res, err.statusCode || 500, { error: err.message || String(err) });
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

  // 预热：先把安装目录和进程状态探一遍，首屏 /api/state 就不用等 tasklist
  (async () => {
    try {
      const st = await loadState();
      await detectApps(st.settings.customPaths);
      await Promise.all(APPS.map((a) => isRunning(a)));
    } catch {}
  })();
  if (!NO_OPEN) {
    spawn('cmd', ['/c', 'start', '', url], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
