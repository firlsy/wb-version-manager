import fs from 'node:fs/promises';
import path from 'node:path';
import { DATA_DIR, cmpVersion, formatBytes, readJson, writeJson } from './store.js';
import {
  DOCS_BASE,
  INTL_UPDATE_FEED,
  UPDATE_FEED,
  parseChangelogPage,
  parseDownloadHistory,
} from './parse.js';

const CATALOG_FILE = path.join(DATA_DIR, 'catalog.json');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36';

let cache = null;
let refreshing = null;

async function fetchText(url, timeout = 20000) {
  const res = await fetch(url, {
    headers: { 'User-Agent': UA, 'Accept-Language': 'zh-CN,zh;q=0.9', 'Cache-Control': 'no-cache' },
    signal: AbortSignal.timeout(timeout),
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return await res.text();
}

function today() {
  return new Date().toISOString().slice(0, 10);
}

export async function loadCatalog() {
  if (cache) return cache;
  const data = await readJson(CATALOG_FILE, null);
  cache = data || { generatedAt: '', sources: {}, latest: null, releases: [] };
  cache.releases = (cache.releases || []).slice().sort((a, b) => cmpVersion(b.version, a.version));
  return cache;
}

export function catalogMeta() {
  if (!cache) return null;
  return {
    generatedAt: cache.generatedAt,
    latest: cache.latest,
    latestIntl: cache.latestIntl || null,
    total: cache.releases.length,
    withNotes: cache.releases.filter((r) => r.notes?.length).length,
    sources: cache.sources,
  };
}

async function fetchLatestFrom(feed) {
  const res = await fetch(`${feed}&version=0.0.0&_t=${Date.now()}`, {
    headers: { 'User-Agent': 'WorkBuddy/5.11.3', 'Cache-Control': 'no-cache' },
    signal: AbortSignal.timeout(15000),
  });
  if (!res.ok) throw new Error(`更新通道 HTTP ${res.status}`);
  const text = await res.text();
  if (!text.trim()) return null;
  const data = JSON.parse(text);
  if (!data?.version) return null;
  return {
    version: String(data.version).split('.').slice(0, 3).join('.'),
    build: String(data.version),
    url: data.url || '',
    sha256: data.sha256hash || '',
    publishedAt: data.timestamp ? new Date(data.timestamp * 1000).toISOString() : '',
  };
}

async function fetchLatest() {
  return fetchLatestFrom(UPDATE_FEED);
}

/** 官方通道不给体积，探一次 Content-Length；HEAD 不行就用 Range 读 Content-Range */
async function probeSize(url) {
  if (!url) return '';
  try {
    const res = await fetch(url, { method: 'HEAD', signal: AbortSignal.timeout(8000) });
    if (res.ok) {
      const n = Number(res.headers.get('content-length') || 0);
      if (n > 0) return formatBytes(n);
    }
  } catch {}
  try {
    const res = await fetch(url, { headers: { Range: 'bytes=0-0' }, signal: AbortSignal.timeout(8000) });
    const total = Number(String(res.headers.get('content-range') || '').split('/')[1] || 0);
    if (total > 0) return formatBytes(total);
  } catch {}
  return '';
}

async function fetchLatestIntl() {
  return fetchLatestFrom(INTL_UPDATE_FEED);
}

async function fetchChangelogVersions() {
  const sm = await fetchText('https://www.workbuddy.cn/docs/sitemap-workbuddy-workbuddy.xml');
  const list = [...sm.matchAll(/<loc>([^<]*Changelog-(\d+\.\d+\.\d+)[^<]*)<\/loc>/g)].map((m) => ({
    url: m[1],
    version: m[2],
  }));
  return [...new Map(list.map((v) => [v.version, v])).values()];
}

async function mapLimit(items, limit, fn) {
  const out = [];
  let i = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (i < items.length) {
      const idx = i++;
      try {
        out[idx] = await fn(items[idx]);
      } catch {
        out[idx] = null;
      }
    }
  });
  await Promise.all(workers);
  return out;
}

/**
 * 增量刷新：更新通道最新版 + 历史版本页 + 缺失的更新日志
 * @param {(msg: string, percent?: number) => void} onProgress
 */
export async function refreshCatalog(onProgress = () => {}) {
  if (refreshing) return refreshing;
  refreshing = (async () => {
    const current = await loadCatalog();
    const merged = new Map(current.releases.map((r) => [r.version, { ...r }]));
    let latest = current.latest;
    let latestIntl = current.latestIntl || null;
    const intlHistory = { ...(current.intlHistory || {}) };
    const report = { added: [], updatedNotes: [], latest: null, latestIntl: null, warnings: [] };

    onProgress('读取官方更新通道…', 8);
    try {
      latest = (await fetchLatest()) || latest;
      report.latest = latest;
    } catch (err) {
      report.warnings.push(`更新通道：${err.message}`);
    }

    // 国际版是独立通道，只发布当前版本；取到的地址累积进 intlHistory
    onProgress('读取国际版更新通道…', 14);
    try {
      const intl = await fetchLatestIntl();
      if (intl && intl.url) {
        latestIntl = intl;
        report.latestIntl = intl;
        // 记下来源：官网以后会撤掉这个地址，界面要标明它是「工具记录」
        intlHistory[intl.version] = { url: intl.url, sizeText: intl.sizeText || '', recorded: true, recordedAt: today() };
        const rel = merged.get(intl.version) || {
          version: intl.version,
          build: intl.build,
          date: '',
          packages: {},
          notes: [],
          notesUrl: '',
          notesTitle: '',
          localOnly: true,
        };
        rel.intlPackages = {
          ...(rel.intlPackages || {}),
          'win32-x64-user': { url: intl.url, sizeText: intl.sizeText || '', recorded: false },
        };
        merged.set(intl.version, rel);
      }
    } catch (err) {
      report.warnings.push(`国际版更新通道：${err.message}`);
    }

    onProgress('读取官网历史版本…', 26);
    try {
      const html = await fetchText(`${DOCS_BASE}/Download-History?_t=${Date.now()}`);
      const parsed = parseDownloadHistory(html);
      for (const [version, rel] of parsed) {
        const old = merged.get(version);
        if (old) {
          merged.set(version, { ...old, ...rel, notes: old.notes?.length ? old.notes : rel.notes, notesUrl: old.notesUrl || rel.notesUrl, notesTitle: old.notesTitle || rel.notesTitle });
        } else {
          merged.set(version, rel);
          report.added.push(version);
        }
      }
    } catch (err) {
      report.warnings.push(`历史版本页：${err.message}`);
    }

    onProgress('读取更新日志索引…', 42);
    let versions = [];
    try {
      versions = await fetchChangelogVersions();
    } catch (err) {
      report.warnings.push(`更新日志索引：${err.message}`);
    }

    const missing = versions.filter((v) => {
      const rel = merged.get(v.version);
      return rel && !rel.notes?.length;
    });
    if (missing.length) {
      let done = 0;
      await mapLimit(missing, 5, async (item) => {
        try {
          const html = await fetchText(item.url, 20000);
          const parsed = parseChangelogPage(html, item.version);
          const rel = merged.get(item.version);
          if (parsed && rel && parsed.notes.length) {
            rel.notes = parsed.notes;
            rel.notesUrl = parsed.url;
            rel.notesTitle = parsed.title;
            report.updatedNotes.push(item.version);
          }
        } finally {
          done++;
          onProgress(`补全更新说明 ${done}/${missing.length}…`, 42 + Math.round((done / missing.length) * 48));
        }
      });
    }

    // 把累积到的国际版地址贴回所有版本（只补不覆盖）
    for (const rel of merged.values()) {
      const hit = intlHistory[rel.version];
      if (!hit || !hit.url) continue;
      if (rel.intlPackages?.['win32-x64-user']?.url) continue;
      rel.intlPackages = {
        ...(rel.intlPackages || {}),
        'win32-x64-user': { url: hit.url, sizeText: hit.sizeText || '', recorded: true, recordedAt: hit.recordedAt || '' },
      };
    }

    // 官方通道不返回体积：给缺体积的包探一次 Content-Length。
    // 分批 4 路并发——串行时一个卡住的 CDN 就能把「刷新目录」拖几分钟；
    // 计数也只在真正需要探测时才加，别被已有体积的条目白白吃掉配额。
    const needProbe = [];
    for (const [version, entry] of Object.entries(intlHistory)) {
      if (!entry?.url || entry.sizeText) continue;
      if (needProbe.length >= 10) break;
      needProbe.push({ version, entry });
    }
    for (let i = 0; i < needProbe.length; i += 4) {
      await Promise.all(
        needProbe.slice(i, i + 4).map(async ({ version, entry }) => {
          const size = await probeSize(entry.url);
          if (!size) return;
          entry.sizeText = size;
          const pkg = merged.get(version)?.intlPackages?.['win32-x64-user'];
          if (pkg && !pkg.sizeText) pkg.sizeText = size;
        })
      );
    }
    if (latest?.url) {
      const pkg = merged.get(latest.version)?.packages?.['win32-x64-user'];
      if (pkg && !pkg.sizeText) {
        const size = await probeSize(latest.url);
        if (size) pkg.sizeText = size;
      }
    }

    // 只有「官网当前发布的那一版」算同步来的，其余都是工具记下来的记录
    const currentIntl = latestIntl?.version || '';
    for (const rel of merged.values()) {
      const pkg = rel.intlPackages?.['win32-x64-user'];
      if (!pkg) continue;
      pkg.recorded = rel.version !== currentIntl;
      if (pkg.recorded && !pkg.recordedAt) pkg.recordedAt = today();
    }

    const releases = [...merged.values()].sort((a, b) => cmpVersion(b.version, a.version));
    cache = {
      generatedAt: new Date().toISOString(),
      sources: {
        history: `${DOCS_BASE}/Download-History`,
        changelog: `${DOCS_BASE}/Changelog`,
        updateFeed: UPDATE_FEED,
        intlUpdateFeed: INTL_UPDATE_FEED,
      },
      latest,
      latestIntl,
      intlHistory,
      releases,
    };
    await writeJson(CATALOG_FILE, cache);
    onProgress('完成', 100);
    return {
      ...report,
      total: releases.length,
      withNotes: releases.filter((r) => r.notes?.length).length,
      generatedAt: cache.generatedAt,
    };
  })().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

export function findRelease(version) {
  return cache?.releases.find((r) => r.version === version) || null;
}

/** 合并本机安装但官方目录未收录的版本（例如内测/灰度包） */
export function mergeLocalVersions(releases, localVersions = []) {
  const map = new Map(releases.map((r) => [r.version, r]));
  for (const v of localVersions) {
    if (!v || map.has(v)) continue;
    map.set(v, {
      version: v,
      build: '',
      date: '',
      packages: {},
      notes: [],
      notesUrl: '',
      notesTitle: '',
      localOnly: true,
    });
  }
  return [...map.values()].sort((a, b) => cmpVersion(b.version, a.version));
}

export { CATALOG_FILE };
