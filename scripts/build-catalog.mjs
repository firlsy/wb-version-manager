// 构建官方版本目录：抓取官网历史版本页 + 更新日志页，生成 data/catalog.json
// 运行：node scripts/build-catalog.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DATA_DIR = path.join(ROOT, 'data');
const OUT_FILE = path.join(DATA_DIR, 'catalog.json');
const DOCS = 'https://www.workbuddy.cn/docs/workbuddy';
const INTL_FEED = 'https://www.workbuddy.ai/v2/update?platform=workbuddy-win32-x64-user';
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36';

const PLATFORM_KEYS = ['darwin-arm64', 'darwin-x64', 'win32-x64-user'];

/** 按链接认平台，别依赖表格列序 */
function platformOfUrl(url) {
  if (/darwin-arm64/i.test(url)) return 'darwin-arm64';
  if (/darwin-x64/i.test(url)) return 'darwin-x64';
  if (/win32-x64-user/i.test(url)) return 'win32-x64-user';
  return '';
}

function stripTags(html) {
  return html
    .replace(/<script[\s\S]*?<\/script>/gi, '')
    .replace(/<style[\s\S]*?<\/style>/gi, '')
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/(p|li|h\d|tr|div)>/gi, '\n')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/[ \t]+/g, ' ')
    .replace(/\n{2,}/g, '\n')
    .trim();
}

async function get(url) {
  const res = await fetch(url, { headers: { 'User-Agent': UA, 'Accept-Language': 'zh-CN,zh;q=0.9' } });
  if (!res.ok) throw new Error(`HTTP ${res.status} ${url}`);
  return await res.text();
}

/** 国际版只发布当前版本，这里取到就记进 intlHistory，别让重建目录把它冲掉 */
async function fetchLatestIntl() {
  try {
    const res = await fetch(`${INTL_FEED}&version=0.0.0`, { headers: { 'User-Agent': 'WorkBuddy/5.11.3' } });
    if (!res.ok) return null;
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
  } catch (err) {
    console.warn('  国际版通道读取失败：', err.message);
    return null;
  }
}

function parseDownloadHistory(html) {
  const releases = new Map();
  const rows = html.match(/<tr>[\s\S]*?<\/tr>/gi) || [];
  for (const row of rows) {
    const cells = [...row.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/gi)].map((m) => m[1]);
    if (cells.length < 3) continue;
    const versionMatch = cells[0].match(/v?(\d+\.\d+\.\d+)/);
    if (!versionMatch) continue;
    const version = versionMatch[1];
    const date = stripTags(cells[1]).trim();
    const packages = {};
    for (let i = 2; i < cells.length; i++) {
      const link = cells[i].match(/href="([^"]+)"/i);
      if (!link) continue;
      const url = link[1];
      const key = platformOfUrl(url) || PLATFORM_KEYS[i - 2];
      if (!key) continue;
      const sizeMatch = stripTags(cells[i]).match(/([\d.]+)\s*(MB|GB|KB)/i);
      packages[key] = {
        url,
        sizeText: sizeMatch ? `${sizeMatch[1]} ${sizeMatch[2].toUpperCase()}` : '',
      };
    }
    if (!Object.keys(packages).length) continue;
    const buildMatch = Object.values(packages)[0].url.match(/WorkBuddy-[a-z0-9-]+-(\d+(?:\.\d+){2,3})-/i);
    releases.set(version, {
      version,
      build: buildMatch ? buildMatch[1] : '',
      date,
      packages,
      notes: [],
      notesUrl: '',
      notesTitle: '',
    });
  }
  return releases;
}

function parseChangelogPage(html, version) {
  const start = html.search(/<h1[\s>]/i);
  const end = html.indexOf('</main>');
  if (start < 0) return null;
  const body = html.slice(start, end > 0 ? end : undefined);
  const heading = body.match(/<h2[^>]*>([\s\S]*?)<\/h2>/i);
  const title = heading ? stripTags(heading[1]) : '';
  const afterHeading = heading ? body.slice(body.indexOf(heading[0]) + heading[0].length) : body;
  const notes = [];
  // 按 h3 分组时保留分组名
  const chunks = afterHeading.split(/<h3[^>]*>/i);
  const first = chunks.shift() || '';
  for (const li of first.matchAll(/<li[^>]*>([\s\S]*?)<\/li>/gi)) {
    const text = stripTags(li[1]);
    if (text) notes.push(text);
  }
  for (const chunk of chunks) {
    const groupName = stripTags(chunk.slice(0, chunk.indexOf('</h3>')));
    const items = [...chunk.matchAll(/<li[^>]*>([\s\S]*?)<\/li>/gi)].map((m) => stripTags(m[1])).filter(Boolean);
    if (!items.length) continue;
    notes.push({ group: groupName, items });
  }
  return { title, notes, url: `${DOCS}/Changelog-${version}` };
}

async function mapLimit(items, limit, fn) {
  const results = [];
  let i = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (i < items.length) {
      const idx = i++;
      try {
        results[idx] = await fn(items[idx], idx);
      } catch (err) {
        results[idx] = null;
        console.warn(`  ! ${items[idx]}: ${err.message}`);
      }
    }
  });
  await Promise.all(workers);
  return results;
}

async function main() {
  console.log('→ 抓取历史版本页…');
  const dhHtml = await get(`${DOCS}/Download-History`);
  const releases = parseDownloadHistory(dhHtml);
  console.log(`  解析到 ${releases.size} 个版本`);

  console.log('→ 抓取文档 sitemap…');
  let changelogVersions = [];
  try {
    const sm = await get('https://www.workbuddy.cn/docs/sitemap-workbuddy-workbuddy.xml');
    changelogVersions = [...sm.matchAll(/<loc>([^<]*Changelog-(\d+\.\d+\.\d+)[^<]*)<\/loc>/g)].map((m) => ({
      url: m[1],
      version: m[2],
    }));
  } catch (err) {
    console.warn('  sitemap 获取失败：', err.message);
  }
  changelogVersions = [...new Map(changelogVersions.map((v) => [v.version, v])).values()];
  console.log(`  发现 ${changelogVersions.length} 个更新日志页`);

  console.log('→ 抓取更新日志…');
  let done = 0;
  await mapLimit(changelogVersions, 5, async (item) => {
    const html = await get(item.url);
    const parsed = parseChangelogPage(html, item.version);
    const rel = releases.get(item.version);
    if (parsed && rel) {
      rel.notes = parsed.notes;
      rel.notesUrl = parsed.url;
      rel.notesTitle = parsed.title;
    }
    done++;
    if (done % 10 === 0) console.log(`  已抓取 ${done}/${changelogVersions.length}`);
  });

  console.log('→ 读取官方更新通道…');
  let latest = null;
  try {
    const res = await fetch('https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user&version=0.0.0', {
      headers: { 'User-Agent': 'WorkBuddy/5.11.3' },
    });
    if (res.ok) {
      const data = await res.json();
      if (data && data.version) {
        latest = {
          version: String(data.version).split('.').slice(0, 3).join('.'),
          build: String(data.version),
          url: data.url,
          sha256: data.sha256hash,
          publishedAt: data.timestamp ? new Date(data.timestamp * 1000).toISOString() : '',
        };
      }
    }
  } catch (err) {
    console.warn('  更新通道读取失败：', err.message);
  }

  const list = [...releases.values()].sort((a, b) => cmpVersion(b.version, a.version));

  // 重建目录不能把国际版那点家底冲掉：先读回旧文件里的 intlHistory，
  // 再问一次国际版通道，最后按版本贴回去。
  console.log('→ 读取国际版更新通道…');
  let previous = null;
  try {
    previous = JSON.parse(await fs.readFile(OUT_FILE, 'utf8'));
  } catch {}
  const intlHistory = { ...(previous?.intlHistory || {}) };
  const intl = await fetchLatestIntl();
  const stamp = new Date().toISOString().slice(0, 10);
  // 每条历史都标成「工具记录」：官网只保留最新版，这些地址官网已经不再提供
  for (const [v, entry] of Object.entries(intlHistory)) {
    if (!entry || !entry.url) continue;
    intlHistory[v] = { ...entry, recorded: true, recordedAt: entry.recordedAt || stamp };
  }
  if (intl?.url) intlHistory[intl.version] = { url: intl.url, sizeText: '', recorded: true, recordedAt: stamp };
  const latestIntl = intl || previous?.latestIntl || null;

  for (const rel of list) {
    const hit = intlHistory[rel.version];
    if (!hit?.url) continue;
    rel.intlPackages = {
      ...(rel.intlPackages || {}),
      'win32-x64-user': {
        url: hit.url,
        sizeText: hit.sizeText || '',
        recorded: rel.version !== intl?.version,
        ...(rel.version !== intl?.version ? { recordedAt: hit.recordedAt || stamp } : {}),
      },
    };
  }

  const catalog = {
    generatedAt: new Date().toISOString(),
    sources: {
      history: `${DOCS}/Download-History`,
      changelog: `${DOCS}/Changelog`,
      updateFeed: 'https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user',
      intlUpdateFeed: INTL_FEED,
    },
    latest,
    latestIntl,
    intlHistory,
    releases: list,
  };

  await fs.mkdir(DATA_DIR, { recursive: true });
  await fs.writeFile(OUT_FILE, JSON.stringify(catalog, null, 2), 'utf8');
  const withNotes = list.filter((r) => r.notes.length).length;
  console.log(`\n✓ 已写入 ${OUT_FILE}`);
  console.log(`  版本 ${list.length} 个（含更新说明 ${withNotes} 个），最新 ${latest?.build || '未知'}`);
  console.log(`  国际版最新 ${latestIntl?.version || '未知'}，累积记录 ${Object.keys(intlHistory).length} 个`);
}

function cmpVersion(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
