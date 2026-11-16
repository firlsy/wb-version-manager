export const DOCS_BASE = 'https://www.workbuddy.cn/docs/workbuddy';
export const UPDATE_FEED = 'https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user';

export const PLATFORM_KEYS = ['darwin-arm64', 'darwin-x64', 'win32-x64-user'];

export const PLATFORM_LABELS = {
  'darwin-arm64': 'macOS · Apple 芯片',
  'darwin-x64': 'macOS · Intel',
  'win32-x64-user': 'Windows · 64 位',
};

export function stripTags(html) {
  return String(html)
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

export function parseDownloadHistory(html) {
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

export function parseChangelogPage(html, version) {
  const start = html.search(/<h1[\s>]/i);
  if (start < 0) return null;
  const end = html.indexOf('</main>');
  const body = html.slice(start, end > 0 ? end : undefined);
  const heading = body.match(/<h2[^>]*>([\s\S]*?)<\/h2>/i);
  const title = heading ? stripTags(heading[1]) : '';
  const afterHeading = heading ? body.slice(body.indexOf(heading[0]) + heading[0].length) : body;
  const notes = [];
  const chunks = afterHeading.split(/<h3[^>]*>/i);
  const first = chunks.shift() || '';
  for (const li of first.matchAll(/<li[^>]*>([\s\S]*?)<\/li>/gi)) {
    const text = stripTags(li[1]);
    if (text) notes.push(text);
  }
  for (const chunk of chunks) {
    const close = chunk.indexOf('</h3>');
    const groupName = close > 0 ? stripTags(chunk.slice(0, close)) : '';
    const items = [...chunk.matchAll(/<li[^>]*>([\s\S]*?)<\/li>/gi)].map((m) => stripTags(m[1])).filter(Boolean);
    if (!items.length) continue;
    notes.push({ group: groupName, items });
  }
  return { title, notes, url: `${DOCS_BASE}/Changelog-${version}` };
}

export function countNotes(notes = []) {
  return notes.reduce((n, item) => n + (typeof item === 'string' ? 1 : item.items?.length || 0), 0);
}
