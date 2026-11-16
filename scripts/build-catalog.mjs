// 构建官方版本目录：抓取官网历史版本页 + 更新日志页，生成 data/catalog.json
// 运行：node scripts/build-catalog.mjs
import fs from 'node:fs/promises';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..');
const DATA_DIR = path.join(ROOT, 'data');
const DOCS = 'https://www.workbuddy.cn/docs/workbuddy';
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36';

const PLATFORM_KEYS = ['darwin-arm64', 'darwin-x64', 'win32-x64-user'];

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
      const key = PLATFORM_KEYS[i - 2];
      if (!key) continue;
      const link = cells[i].match(/href="([^"]+)"/i);
      if (!link) continue;
      const sizeMatch = stripTags(cells[i]).match(/([\d.]+)\s*(MB|GB|KB)/i);
      packages[key] = {
        url: link[1],
        sizeText: sizeMatch ? `${sizeMatch[1]} ${sizeMatch[2].toUpperCase()}` : '',
      };
    }
    if (!Object.keys(packages).length) continue;
    const buildMatch = Object.values(packages)[0].url.match(/WorkBuddy-[a-z0-9-]+-(\d+\.\d+\.\d+\.\d+)-/i);
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
  const catalog = {
    generatedAt: new Date().toISOString(),
    sources: {
      history: `${DOCS}/Download-History`,
      changelog: `${DOCS}/Changelog`,
      updateFeed: 'https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user',
    },
    latest,
    releases: list,
  };

  await fs.mkdir(DATA_DIR, { recursive: true });
  const out = path.join(DATA_DIR, 'catalog.json');
  await fs.writeFile(out, JSON.stringify(catalog, null, 2), 'utf8');
  const withNotes = list.filter((r) => r.notes.length).length;
  console.log(`\n✓ 已写入 ${out}`);
  console.log(`  版本 ${list.length} 个（含更新说明 ${withNotes} 个），最新 ${latest?.build || '未知'}`);
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
