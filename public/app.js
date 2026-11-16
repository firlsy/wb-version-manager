const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

const state = {
  apps: [],
  catalog: null,
  settings: {},
  jobs: [],
  cached: [],
  guard: null,
  dataDirs: [],
  selectedAppId: 'workbuddy',
  versions: [],
  installedVersion: '',
  lock: null,
  latest: null,
  search: '',
  series: 'all',
  filter: 'all',
  selection: new Set(),
  drawerVersion: null,
  polling: null,
  refreshing: false,
};

const PLATFORM_ORDER = ['win32-x64-user', 'darwin-arm64', 'darwin-x64'];
const PLATFORM_LABELS = {
  'win32-x64-user': 'Windows · 64 位',
  'darwin-arm64': 'macOS · Apple 芯片',
  'darwin-x64': 'macOS · Intel',
};

/* ---------------- api ---------------- */
async function apiGet(url) {
  const res = await fetch(url, { cache: 'no-store' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `请求失败 ${res.status}`);
  return data;
}
async function apiPost(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `请求失败 ${res.status}`);
  return data;
}
async function apiDelete(url) {
  const res = await fetch(url, { method: 'DELETE' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `请求失败 ${res.status}`);
  return data;
}

/* ---------------- utils ---------------- */
function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function fmtDate(d) {
  if (!d) return '—';
  return String(d).slice(0, 10);
}
function appById(id) {
  return state.apps.find((a) => a.id === id) || null;
}
function activeApp() {
  return appById(state.selectedAppId);
}
function toast(message, type = '') {
  const el = document.createElement('div');
  el.className = `toast ${type}`;
  el.innerHTML = `<span class="t-icon">${type === 'ok' ? '✓' : type === 'err' ? '!' : 'i'}</span><span>${esc(message)}</span>`;
  $('#toasts').appendChild(el);
  setTimeout(() => {
    el.style.opacity = '0';
    el.style.transform = 'translateY(6px)';
    el.style.transition = 'all .2s';
    setTimeout(() => el.remove(), 220);
  }, 3200);
}
async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    ta.remove();
    return ok;
  }
}
function svgIcon(name) {
  const paths = {
    download: '<path d="M12 3v11m0 0 4-4m-4 4-4-4M4 20h16"/>',
    copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
    lock: '<rect x="4" y="10" width="16" height="10" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
    unlock: '<rect x="4" y="10" width="16" height="10" rx="2"/><path d="M8 10V7a4 4 0 0 1 7.4-2"/>',
    external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    check: '<path d="m5 12 5 5L20 7"/>',
    alert: '<path d="M12 9v4m0 4h.01M10.3 3.9 2 18a2 2 0 0 0 1.7 3h16.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/>',
  };
  return `<svg viewBox="0 0 24 24" class="ico">${paths[name] || ''}</svg>`;
}

/* ---------------- data ---------------- */
async function loadState() {
  const data = await apiGet('/api/state');
  state.apps = data.apps;
  state.catalog = data.catalog;
  state.settings = data.settings;
  state.jobs = data.jobs;
  state.cached = data.cached;
  state.guard = data.guard;
  state.quarantine = data.quarantine;
  state.dataDirs = data.dataDirs;
  state.lastInstall = data.lastInstall;
  if (!state.apps.some((a) => a.id === state.selectedAppId)) {
    state.selectedAppId = state.apps[0]?.id || 'workbuddy';
  }
  updateSource(data.catalog, data.refresh);
  if (data.refresh?.running) setRefreshBar(true, data.refresh.percent, data.refresh.message);
  renderShell();
}

async function loadVersions() {
  const data = await apiGet(`/api/versions?appId=${encodeURIComponent(state.selectedAppId)}`);
  state.versions = data.releases;
  state.installedVersion = data.installedVersion;
  state.lock = data.lock;
  state.latest = data.latest;
  state.generatedAt = data.generatedAt;
  renderVersions();
}

async function reloadAll() {
  await loadState();
  await loadVersions();
}

/* ---------------- render: shell ---------------- */
function updateSource(catalog, refresh) {
  const dot = $('#sourceDot');
  const pill = $('#sourcePill');
  const text = $('#sourceText');
  if (refresh?.running) {
    pill.className = 'source-pill warn';
    text.textContent = refresh.message || '同步中…';
    return;
  }
  if (catalog?.generatedAt) {
    pill.className = 'source-pill ok';
    text.textContent = `官方源 · ${catalog.total} 个版本`;
  } else {
    pill.className = 'source-pill warn';
    text.textContent = '离线数据';
  }
}

function renderShell() {
  renderHeroStats();
  renderAppCards();
  renderDrift();
  renderBlock();
  renderSwitch();
  renderDownloads();
  renderFooter();
}

function renderBlock() {
  const q = state.quarantine;
  if (!q) return;
  const badge = $('#blockBadge');
  const body = $('#blockBody');
  const actions = $('#blockActions');
  const staged = q.staged || { kind: 'none' };

  if (q.locked) {
    badge.innerHTML = '<span class="badge green">已拦截</span>';
    actions.innerHTML = `<button class="btn btn-sm" data-act="block-off">解除拦截</button>`;
    body.innerHTML = `
      <div class="block-state on">
        <div class="block-icon">${svgIcon('lock')}</div>
        <div class="block-main">
          <b>自动更新已被拦截</b>
          <p>更新器缓存位置已放置同名占位目录，客户端无法把新安装包落盘；已安装的客户端不受影响。</p>
          <p class="mono">${esc(q.updaterDir)}\\installer.exe</p>
        </div>
        <div class="block-side"><span class="badge gray">隔离区 ${q.vault.length} 个包</span></div>
      </div>`;
    return;
  }

  if (staged.kind === 'file') {
    badge.innerHTML = '<span class="badge amber">有待安装更新</span>';
    actions.innerHTML = `<button class="btn btn-sm btn-primary" data-act="block-on">拦截并隔离</button>`;
    body.innerHTML = `
      <div class="block-state warn">
        <div class="block-icon">${svgIcon('alert')}</div>
        <div class="block-main">
          <b>发现已下载、等待安装的更新包</b>
          <p>${esc(staged.sizeText)} · ${new Date(staged.mtime).toLocaleString('zh-CN', { hour12: false })} · 下次启动客户端时会静默安装</p>
          <p class="mono">${esc(q.updaterDir)}\\installer.exe</p>
        </div>
        <div class="block-side"><span class="badge gray">隔离区 ${q.vault.length} 个包</span></div>
      </div>`;
    return;
  }

  badge.innerHTML = '<span class="badge gray">未拦截</span>';
  actions.innerHTML = `<button class="btn btn-sm" data-act="block-on">开启拦截</button>`;
  body.innerHTML = `
    <div class="block-state">
      <div class="block-icon">${svgIcon('unlock')}</div>
      <div class="block-main">
        <b>当前没有待安装的更新</b>
        <p>开启拦截后，客户端后续下载的更新包会被隔离，直到你主动解除。</p>
        <p class="mono">${esc(q.updaterDir)}</p>
      </div>
      <div class="block-side"><span class="badge gray">隔离区 ${q.vault.length} 个包</span></div>
    </div>`;
}

function renderHeroStats() {
  const total = state.catalog?.total || 0;
  const notes = state.catalog?.withNotes || 0;
  const latest = state.catalog?.latest?.version || '—';
  $('#heroStats').innerHTML = `
    <div class="hero-stat"><b>${total}</b><span>可回退版本</span></div>
    <div class="hero-stat"><b>${notes}</b><span>含官方更新说明</span></div>
    <div class="hero-stat"><b>${esc(latest)}</b><span>官方最新</span></div>`;
}

function renderAppCards() {
  const grid = $('#appGrid');
  grid.innerHTML = state.apps
    .map((app) => {
      const active = app.id === state.selectedAppId;
      const info = app.install || {};
      const version = info.version || '';
      const badges = [];
      if (app.lock) badges.push(`<span class="badge amber">${svgIcon('lock')}已锁定 ${esc(app.lock.version)}</span>`);
      if (app.drift) badges.push(`<span class="badge red">版本漂移</span>`);
      if (app.hasUpdate && !app.drift) badges.push(`<span class="badge blue">可升级 ${esc(app.latest?.version || '')}</span>`);
      if (app.upToDate && !app.lock) badges.push('<span class="badge green">已是最新</span>');
      if (!info.found) badges.push('<span class="badge gray">未检测到</span>');
      const lockBtn = app.lock
        ? `<button class="btn btn-sm" data-act="unlock" data-app="${app.id}">${svgIcon('unlock')}解除锁定</button>`
        : `<button class="btn btn-sm btn-primary" data-act="lock-current" data-app="${app.id}" ${version ? '' : 'disabled'}>${svgIcon('lock')}锁定当前版本</button>`;
      return `
      <article class="app-card ${active ? 'active' : ''} ${app.id === 'workbuddyai' ? 'ai' : ''}" data-app-card="${app.id}">
        <div class="app-card-head">
          <div class="app-logo">${app.id === 'workbuddyai' ? 'AI' : 'WB'}</div>
          <div style="min-width:0">
            <div class="app-name"><b>${esc(app.name)}</b><span class="edition">${esc(app.edition)}</span></div>
            <div class="app-path" title="${esc(info.installDir || '未检测到安装目录')}">${esc(info.installDir || '未检测到安装目录')}</div>
          </div>
        </div>
        <div class="app-version-row">
          <div class="app-version">
            <div class="label">当前版本</div>
            <div class="value ${version ? '' : 'missing'}">${esc(version || '未安装')}</div>
          </div>
          <div class="app-status">${badges.join('') || '<span class="badge gray">未锁定</span>'}</div>
        </div>
        <div class="app-actions">
          ${lockBtn}
          <button class="btn btn-sm btn-ghost" data-act="settings" data-app="${app.id}">目录设置</button>
        </div>
      </article>`;
    })
    .join('');
}

function renderDrift() {
  const area = $('#driftArea');
  const drifted = state.apps.filter((a) => a.drift);
  if (!drifted.length) {
    area.innerHTML = '';
    return;
  }
  area.innerHTML = drifted
    .map(
      (app) => `
    <div class="drift">
      <div class="drift-icon">${svgIcon('alert')}</div>
      <div class="drift-text">
        <b>${esc(app.name)} 的版本被改动了</b>
        <p>锁定版本 ${esc(app.lock.version)}，当前安装 ${esc(app.install.version)}。回退不会影响工作区、任务与技能数据。</p>
      </div>
      <div class="drift-actions">
        <button class="btn btn-sm" data-act="download" data-version="${esc(app.lock.version)}" data-app="${app.id}">先下载安装包</button>
        <button class="btn btn-sm btn-primary" data-act="rollback" data-version="${esc(app.lock.version)}" data-app="${app.id}">一键回退</button>
      </div>
    </div>`
    )
    .join('');
}

function renderSwitch() {
  $('#appSwitch').innerHTML = state.apps
    .map(
      (a) =>
        `<button class="${a.id === state.selectedAppId ? 'active' : ''}" data-switch="${a.id}">${esc(a.name)}</button>`
    )
    .join('');
}

function renderFooter() {
  const at = state.catalog?.generatedAt ? new Date(state.catalog.generatedAt) : null;
  $('#catalogStamp').textContent = at
    ? `目录同步于 ${at.toLocaleString('zh-CN', { hour12: false })}`
    : '尚未同步官方目录';
}

/* ---------------- render: versions ---------------- */
function filteredVersions() {
  const q = state.search.trim().toLowerCase();
  return state.versions.filter((r) => {
    if (state.series !== 'all' && r.series !== state.series) return false;
    if (state.filter === 'notes' && !r.noteCount) return false;
    if (state.filter === 'downloaded' && !r.downloaded) return false;
    if (q) {
      const hay = `${r.version} ${r.build || ''} ${r.date || ''}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
}

function renderVersions() {
  const app = activeApp();
  const rows = filteredVersions();
  const sub = $('#versionsSub');
  sub.textContent = app
    ? `为 ${app.name} 管理版本 · 共 ${state.versions.length} 个版本 · ${state.versions.filter((r) => r.noteCount).length} 个含官方更新说明`
    : '正在读取…';

  const seriesList = [...new Set(state.versions.map((r) => r.series))];
  const sel = $('#seriesSelect');
  sel.innerHTML = `<option value="all">全部系列</option>` + seriesList.map((s) => `<option value="${s}">${s}</option>`).join('');
  sel.value = state.series;

  $('#versionRows').innerHTML = rows
    .map((r) => {
      const badges = [];
      if (r.installed) badges.push('<span class="badge blue">当前安装</span>');
      if (r.locked) badges.push(`<span class="badge amber">${svgIcon('lock')}锁定中</span>`);
      if (r.latest) badges.push('<span class="badge green">官方最新</span>');
      if (r.downloaded) badges.push('<span class="badge gray">已下载</span>');
      if (r.localOnly) badges.push('<span class="badge gray">本机版本</span>');
      if (!r.noteCount) badges.push('<span class="badge gray">无说明</span>');
      const checked = state.selection.has(r.version);
      return `
      <tr data-version="${esc(r.version)}" class="${checked ? 'selected' : ''} ${r.locked ? 'locked-row' : ''}">
        <td class="col-check"><input type="checkbox" class="checkbox row-check" ${checked ? 'checked' : ''} data-check="${esc(r.version)}" /></td>
        <td>
          <div class="version-cell">
            <span class="version-num">${esc(r.version)}</span>
          </div>
          <div class="version-sub">${r.build ? esc(r.build) : '构建号未知'}</div>
        </td>
        <td class="row-date">${fmtDate(r.date)}</td>
        <td class="row-size">${esc(r.winSize || '—')}</td>
        <td><div class="row-state">${badges.join('')}</div></td>
        <td>
          <div class="row-actions">
            <button class="link-btn" data-act="detail" data-version="${esc(r.version)}">详情</button>
            <button class="link-btn" data-act="download" data-version="${esc(r.version)}" data-app="${app?.id || ''}" ${r.winUrl ? '' : 'disabled'}>下载</button>
            <button class="link-btn muted" data-act="copy" data-version="${esc(r.version)}" ${r.winUrl ? '' : 'disabled'} title="复制 Windows 安装包链接">${svgIcon('copy')}</button>
          </div>
        </td>
      </tr>`;
    })
    .join('');

  $('#versionsEmpty').hidden = rows.length > 0;
  $('#checkAll').checked = rows.length > 0 && rows.every((r) => state.selection.has(r.version));
  $('#checkAll').indeterminate = rows.some((r) => state.selection.has(r.version)) && !$('#checkAll').checked;
  renderSelectionBar();
}

function renderSelectionBar() {
  const bar = $('#selectionBar');
  const n = state.selection.size;
  bar.hidden = n === 0;
  if (!n) return;
  $('#selCount').textContent = `已选 ${n} 个版本`;
  const lockBtn = $('[data-sel="lock"]');
  lockBtn.disabled = n !== 1;
  lockBtn.title = n === 1 ? '把应用锁定到所选版本' : '一次只能锁定一个版本';
}

/* ---------------- render: downloads ---------------- */
function renderDownloads() {
  const area = $('#downloadsArea');
  const jobs = state.jobs || [];
  const cached = state.cached || [];
  const jobHtml = jobs
    .map((j) => {
      const app = appById(j.appId);
      const statusText =
        j.status === 'downloading'
          ? `${j.percent}% · ${j.speedText || '连接中'}`
          : j.status === 'done'
          ? '已完成'
          : j.status === 'cancelled'
          ? '已取消'
          : `失败：${j.error}`;
      return `
      <div class="dl-item">
        <div class="dl-icon">${svgIcon('download')}</div>
        <div class="dl-main">
          <div class="dl-name">${esc(app?.name || j.appId)} ${esc(j.version)} · ${esc(j.filename)}</div>
          <div class="dl-meta"><span>${statusText}</span><span>${(j.received / 1048576).toFixed(1)} / ${j.total ? (j.total / 1048576).toFixed(1) : '?'} MB</span></div>
          <div class="progress"><i style="width:${j.status === 'done' ? 100 : j.percent}%"></i></div>
        </div>
        <div class="dl-actions">
          ${j.status === 'downloading' ? `<button class="btn btn-sm" data-act="cancel-dl" data-id="${esc(j.id)}">取消</button>` : ''}
          ${j.status === 'done' ? `<button class="btn btn-sm btn-primary" data-act="install" data-version="${esc(j.version)}" data-app="${esc(j.appId)}">安装</button>` : ''}
        </div>
      </div>`;
    })
    .join('');

  const cachedHtml = cached
    .filter((c) => c.platform === 'win32-x64-user')
    .map((c) => {
      const app = appById(c.appId);
      return `
      <div class="dl-item">
        <div class="dl-icon">${svgIcon('folder')}</div>
        <div class="dl-main">
          <div class="dl-name">${esc(c.filename)}</div>
          <div class="dl-meta"><span>${esc(app?.name || c.appId)}</span><span>${esc(c.sizeText)}</span><span>${new Date(c.mtime).toLocaleString('zh-CN', { hour12: false })}</span></div>
        </div>
        <div class="dl-actions">
          <button class="btn btn-sm" data-act="reveal" data-path="${esc(c.path)}" title="在资源管理器中显示">${svgIcon('folder')}</button>
          <button class="btn btn-sm btn-primary" data-act="install" data-version="${esc(c.versionShort || c.version)}" data-app="${esc(c.appId)}">安装</button>
        </div>
      </div>`;
    })
    .join('');

  area.innerHTML = `<div class="dl-list">${jobHtml}${cachedHtml}${!jobHtml && !cachedHtml ? '<div class="dl-empty">还没有下载记录。在版本清单里点「下载」，安装包会保存到工具目录。</div>' : ''}</div>`;
}

/* ---------------- drawer ---------------- */
function findVersion(version) {
  return state.versions.find((v) => v.version === version) || null;
}

function openDrawer(version) {
  const r = findVersion(version);
  if (!r) return;
  state.drawerVersion = version;
  const app = activeApp();
  $('#drawerEyebrow').textContent = `${app?.name || ''} · 版本详情`;
  $('#drawerTitle').textContent = r.version;
  $('#drawerMeta').textContent = [r.date ? `发布于 ${r.date}` : '', r.build || '', r.noteCount ? `${r.noteCount} 条更新` : '官方未提供说明']
    .filter(Boolean)
    .join(' · ');

  let notesHtml = '';
  if (r.notes?.length) {
    const groups = [];
    let current = { group: '', items: [] };
    for (const n of r.notes) {
      if (typeof n === 'string') current.items.push(n);
      else {
        if (current.items.length) groups.push(current);
        current = { group: n.group, items: n.items };
      }
    }
    if (current.items.length) groups.push(current);
    notesHtml = groups
      .map(
        (g) => `
      <div class="notes-group">
        ${g.group ? `<h4>${esc(g.group)}</h4>` : ''}
        <ul class="notes">${g.items
          .map((t) => `<li class="${/^修复/.test(t) ? 'fix' : /^新增/.test(t) ? 'new' : ''}">${esc(t)}</li>`)
          .join('')}</ul>
      </div>`
      )
      .join('');
  } else {
    notesHtml = `<div class="note-empty">官方更新日志未收录 ${esc(r.version)} 的说明。可以打开官方原文页查看，或参考相邻版本的更新内容。</div>`;
  }

  const pkgs = PLATFORM_ORDER.filter((p) => r.packages?.[p])
    .map((p) => {
      const pkg = r.packages[p];
      return `
      <div class="pkg">
        <div class="pkg-main">
          <div class="pkg-platform">${PLATFORM_LABELS[p] || p} <span class="pkg-size">${esc(pkg.sizeText || '')}</span></div>
          <div class="pkg-url" title="${esc(pkg.url)}">${esc(pkg.url)}</div>
        </div>
        <div class="pkg-actions">
          <button class="icon-btn sm" data-act="copy-url" data-url="${esc(pkg.url)}" title="复制链接">${svgIcon('copy')}</button>
          ${p === 'win32-x64-user' ? `<button class="icon-btn sm" data-act="download" data-version="${esc(r.version)}" data-app="${app?.id || ''}" title="下载">${svgIcon('download')}</button>` : ''}
        </div>
      </div>`;
    })
    .join('');

  $('#drawerBody').innerHTML = `
    ${notesHtml}
    <div class="section-label">安装包与链接</div>
    ${pkgs || '<div class="note-empty">官方历史版本页未提供该版本的安装包链接。</div>'}
    ${r.notesUrl ? `<div style="margin-top:14px"><a class="link-btn" href="${esc(r.notesUrl)}" target="_blank" rel="noreferrer">${svgIcon('external')} 查看官方更新日志原文</a></div>` : ''}
  `;

  const isLocked = state.lock?.version === r.version;
  const cached = state.cached.some((c) => c.appId === app?.id && (c.versionShort === r.version || c.version === r.version));
  $('#drawerFoot').innerHTML = `
    <button class="btn ${isLocked ? '' : 'btn-primary'}" data-act="${isLocked ? 'unlock' : 'lock'}" data-version="${esc(r.version)}" data-app="${app?.id || ''}">
      ${isLocked ? svgIcon('unlock') + '解除锁定' : svgIcon('lock') + '锁定到此版本'}
    </button>
    ${
      cached
        ? `<button class="btn" data-act="install" data-version="${esc(r.version)}" data-app="${app?.id || ''}">安装此版本</button>`
        : `<button class="btn" data-act="download" data-version="${esc(r.version)}" data-app="${app?.id || ''}" ${r.winUrl ? '' : 'disabled'}>${svgIcon('download')}下载安装包</button>`
    }`;

  $('#drawer').hidden = false;
  $('#drawerMask').hidden = false;
  try {
    history.replaceState(null, '', `#${version}`);
  } catch {}
}

function closeDrawer() {
  $('#drawer').hidden = true;
  $('#drawerMask').hidden = true;
  state.drawerVersion = null;
  try {
    history.replaceState(null, '', location.pathname);
  } catch {}
}

/* ---------------- modal ---------------- */
let modalOnClose = null;
function showModal({ title, body, actions = [], wide = false, onClose = null, dismissible = true }) {
  $('#modalTitle').textContent = title;
  $('#modalBody').innerHTML = body;
  $('#modalFoot').innerHTML = actions
    .map((a, i) => `<button class="btn ${a.kind === 'primary' ? 'btn-primary' : a.kind === 'danger' ? 'btn-danger' : ''}" data-modal-action="${i}">${esc(a.label)}</button>`)
    .join('');
  $('#modalClose').style.display = dismissible ? '' : 'none';
  modalOnClose = onClose;
  $('#modal').style.width = wide ? 'min(720px, 96vw)' : '';
  $('#modalMask').hidden = false;
  $$('#modalFoot [data-modal-action]').forEach((btn, i) => {
    btn.onclick = async () => {
      const action = actions[i];
      if (action.onClick) {
        const keep = await action.onClick();
        if (keep === false) return;
      }
      if (action.keepOpen !== true) hideModal();
    };
  });
}
function hideModal() {
  $('#modalMask').hidden = true;
  $('#modal').style.width = '';
  if (modalOnClose) {
    const fn = modalOnClose;
    modalOnClose = null;
    fn();
  }
}

/* ---------------- actions ---------------- */
async function selectApp(appId) {
  if (state.selectedAppId === appId) return;
  state.selectedAppId = appId;
  state.selection.clear();
  state.search = '';
  state.series = 'all';
  state.filter = 'all';
  $('#searchInput').value = '';
  $$('#filterChips .chip').forEach((c) => c.classList.toggle('active', c.dataset.filter === 'all'));
  renderAppCards();
  renderSwitch();
  await loadVersions();
}

async function toggleLock(version, appId = state.selectedAppId) {
  const app = appById(appId);
  const current = app?.lock?.version;
  if (current === version) {
    await apiDelete(`/api/lock/${appId}`);
    toast(`已解除 ${app.name} 的版本锁定`, 'ok');
  } else {
    const res = await apiPost('/api/lock', { appId, version, build: findVersion(version)?.build || '' });
    if (res.block?.error) {
      toast(`已锁定 ${version}，但拦截更新失败：${res.block.error}`, 'err');
    } else {
      toast(`已锁定 ${app.name} 到 ${version}，自动更新已拦截`, 'ok');
    }
  }
  await reloadAll();
  if (state.drawerVersion) openDrawer(state.drawerVersion);
}

async function downloadVersion(version, appId = state.selectedAppId, silent = false) {
  const r = findVersion(version) || state.versions.find((v) => v.version === version);
  const url = r?.winUrl || r?.packages?.['win32-x64-user']?.url;
  if (!url) {
    toast('该版本没有可用的 Windows 安装包', 'err');
    return;
  }
  try {
    await apiPost('/api/download', { appId, version, url });
    if (!silent) toast(`开始下载 ${version} 的 Windows 安装包`);
    await refreshDownloads();
    startPolling();
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function refreshDownloads() {
  const data = await apiGet('/api/downloads');
  state.jobs = data.jobs;
  state.cached = data.cached;
  renderDownloads();
}

function installConfirm(version, appId) {
  const app = appById(appId);
  const cached = state.cached.find((c) => c.appId === appId && (c.versionShort === version || c.version === version));
  const current = app?.install?.version || '未知';
  const dataRows = state.dataDirs
    .map((d) => `<li><span class="ok">✓</span><span>${esc(d.label)}：<span class="mono">${esc(d.path)}</span>${d.exists ? '' : '（未创建）'}</span></li>`)
    .join('');
  showModal({
    title: `安装 ${app?.name || ''} ${version}`,
    body: `
      <p>将把 <strong>${esc(app?.name || '')}</strong> 从 <span class="mono">${esc(current)}</span> 切换到 <span class="mono">${esc(version)}</span>。</p>
      <dl class="kv" style="margin-top:12px">
        <dt>安装目录</dt><dd class="mono">${esc(app?.install?.installDir || '未检测到')}</dd>
        <dt>安装包</dt><dd class="mono">${esc(cached?.filename || '本地缓存')}</dd>
        <dt>安装方式</dt><dd>官方 NSIS 静默安装（/S --updated /UPDATE=1）</dd>
      </dl>
      <p style="margin-top:12px">以下数据不会被安装器改动：</p>
      <ul class="safe-list">${dataRows}</ul>
      <p style="margin-top:10px;color:var(--muted)">安装前会关闭正在运行的客户端；系统会弹出 UAC 管理员授权，请选择「是」。</p>`,
    actions: [
      { label: '取消' },
      {
        label: '开始安装',
        kind: 'primary',
        keepOpen: true,
        onClick: async () => {
          const body = `
            <div style="display:flex;align-items:center;gap:10px;padding:8px 0 4px">
              <svg viewBox="0 0 24 24" class="ico spin" style="width:20px;height:20px;color:var(--accent)"><path d="M21 12a9 9 0 1 1-6.2-8.5"/></svg>
              <b>正在安装，请在 UAC 弹窗中允许</b>
            </div>
            <p style="margin-top:8px">安装过程通常需要 1–3 分钟，完成后会自动校验版本号。</p>`;
          showModal({ title: '安装进行中', body, actions: [], dismissible: false });
          try {
            const res = await apiPost('/api/install', { appId, version });
            await reloadAll();
            if (res.ok) {
              showModal({
                title: '安装完成',
                body: `<p>已切换到 <strong>${esc(version)}</strong>。当前检测版本：<span class="mono">${esc(res.result?.after?.version || version)}</span>。</p>
                       <p style="color:var(--muted)">工作区、任务、技能与资料库均未改动。</p>`,
                actions: [{ label: '知道了', kind: 'primary' }],
              });
              toast(`已安装 ${version}`, 'ok');
            } else {
              showModal({
                title: '安装未完成',
                body: `<p>安装器退出码：<span class="mono">${esc(res.result?.exitCode)}</span>，当前版本：<span class="mono">${esc(res.result?.after?.version || '未知')}</span>。</p>
                       <p style="color:var(--muted)">${esc(res.result?.stderr || '可能是 UAC 被拒绝或安装被中断，可重试一次。')}</p>`,
                actions: [{ label: '关闭' }],
              });
            }
          } catch (err) {
            showModal({ title: '安装失败', body: `<p>${esc(err.message)}</p>`, actions: [{ label: '关闭' }] });
          }
        },
      },
    ],
  });
}

function exportModal(items, defaultName = 'wb-packages') {
  if (!items.length) {
    toast('没有可导出的链接', 'err');
    return;
  }
  const preview = (fmt) => items.map((it) => (fmt === 'json' ? JSON.stringify(it) : fmt === 'csv' ? `${it.appName},${it.version},${it.url}` : it.url)).join('\n');
  showModal({
    title: `导出 ${items.length} 条安装包链接`,
    wide: true,
    body: `
      <div style="display:flex;gap:8px;align-items:center;margin-bottom:10px">
        <select class="select" id="exportFormat">
          <option value="txt">纯链接（每行一条）</option>
          <option value="csv">CSV 表格</option>
          <option value="markdown">Markdown 列表</option>
          <option value="json">JSON</option>
        </select>
        <span style="font-size:12px;color:var(--muted)">导出后可直接复制，或保存到工具目录 exports/</span>
      </div>
      <textarea class="textarea" id="exportPreview" readonly></textarea>`,
    actions: [
      { label: '关闭' },
      {
        label: '复制',
        onClick: async () => {
          const fmt = $('#exportFormat').value;
          const ok = await copyText(preview(fmt));
          toast(ok ? '已复制到剪贴板' : '复制失败，请手动选择文本', ok ? 'ok' : 'err');
          return false;
        },
      },
      {
        label: '保存到文件',
        kind: 'primary',
        onClick: async () => {
          const fmt = $('#exportFormat').value;
          const ext = fmt === 'markdown' ? 'md' : fmt;
          const res = await apiPost('/api/export', {
            items,
            format: fmt,
            filename: `${defaultName}-${new Date().toISOString().slice(0, 10)}.${ext}`,
          });
          toast(`已保存：${res.path}`, 'ok');
        },
      },
    ],
  });
  const update = () => {
    $('#exportPreview').value = preview($('#exportFormat').value);
  };
  $('#exportFormat').onchange = update;
  update();
}

function selectedItems() {
  const app = activeApp();
  return [...state.selection]
    .map((v) => findVersion(v))
    .filter(Boolean)
    .map((r) => ({
      appId: app?.id || '',
      appName: app?.name || '',
      version: r.version,
      platform: 'win32-x64-user',
      date: r.date || '',
      sizeText: r.winSize || '',
      url: r.winUrl || '',
    }))
    .filter((it) => it.url);
}

function filteredItems() {
  const app = activeApp();
  return filteredVersions()
    .filter((r) => r.winUrl)
    .map((r) => ({
      appId: app?.id || '',
      appName: app?.name || '',
      version: r.version,
      platform: 'win32-x64-user',
      date: r.date || '',
      sizeText: r.winSize || '',
      url: r.winUrl,
    }));
}

/* ---------------- settings ---------------- */
function settingsModal() {
  const app = activeApp();
  const guard = state.guard || {};
  const rows = state.apps
    .map(
      (a) => `
    <div class="setting-row">
      <div style="min-width:0;flex:1">
        <b>${esc(a.name)} 安装目录</b>
        <span>${a.install?.found ? '已自动检测' : '未检测到，可手动指定'}</span>
        <input class="input" id="path-${a.id}" style="margin-top:6px" value="${esc(a.install?.installDir || '')}" placeholder="例如 D:\\Program Files\\${esc(a.name)}" />
      </div>
    </div>`
    )
    .join('');

  showModal({
    title: '设置',
    wide: true,
    body: `
      ${rows}
      <div class="setting-row">
        <div style="flex:1">
          <b>后台版本守护</b>
          <span>每 30 分钟检查一次版本；发现被自动升级时按锁定版本回退（计划任务，需管理员权限）</span>
          <div style="font-size:11.5px;color:var(--muted);margin-top:4px">${guard.installed ? `已启用 · 下次运行 ${esc(guard.nextRun || '待定')}` : '未启用'}</div>
        </div>
        <label class="switch"><input type="checkbox" id="guardToggle" ${state.settings.guard ? 'checked' : ''}/><i></i></label>
      </div>
      <div class="section-label">数据位置（安装器不会改动）</div>
      ${state.dataDirs.map((d) => `<div class="setting-row"><div style="min-width:0"><b>${esc(d.label)}</b><span class="mono" style="word-break:break-all">${esc(d.path)}</span></div><span class="badge ${d.exists ? 'green' : 'gray'}">${d.exists ? '存在' : '未创建'}</span></div>`).join('')}
      <div class="setting-row">
        <div><b>安装包缓存</b><span class="mono">${esc(state.storeDir || '')}</span></div>
        <button class="btn btn-sm" data-act="reveal" data-path="${esc(state.storeDir || '')}">打开</button>
      </div>
      <div class="section-label">官方数据源</div>
      <div class="setting-row"><div><b>更新通道</b><span class="mono">copilot.tencent.com/v2/update · workbuddy-win32-x64-user</span></div></div>
      <div class="setting-row"><div><b>版本与更新日志</b><span class="mono">www.workbuddy.cn/docs/workbuddy/Download-History</span></div></div>
      ${app?.install?.found ? '' : '<p style="color:var(--red);font-size:12px;margin-top:8px">当前应用未检测到安装目录，安装/回退前请先指定正确路径。</p>'}
    `,
    actions: [
      { label: '取消' },
      {
        label: '保存',
        kind: 'primary',
        onClick: async () => {
          const customPaths = { ...(state.settings.customPaths || {}) };
          for (const a of state.apps) {
            const val = $(`#path-${a.id}`)?.value.trim();
            if (val) customPaths[a.id] = val;
            else delete customPaths[a.id];
          }
          const wantGuard = $('#guardToggle').checked;
          const res = await apiPost('/api/settings', {
            settings: { ...state.settings, customPaths },
            guard: wantGuard !== Boolean(state.settings.guard) ? wantGuard : undefined,
          });
          if (res.guard && res.guard.ok === false) {
            toast(`守护任务创建失败：${res.guard.output || '需要管理员权限'}`, 'err');
          } else if (wantGuard) {
            toast('已启用后台版本守护', 'ok');
          }
          await reloadAll();
        },
      },
    ],
  });
}

/* ---------------- refresh ---------------- */
function setRefreshBar(on, percent = 0, message = '') {
  const bar = $('#refreshBar');
  bar.classList.toggle('on', on);
  $('#refreshFill').style.width = `${Math.max(2, Math.min(100, percent))}%`;
  if (message) $('#sourceText').textContent = message;
}

async function doRefresh() {
  if (state.refreshing) return;
  state.refreshing = true;
  const btn = $('#refreshBtn');
  btn.disabled = true;
  btn.querySelector('.ico')?.classList.add('spin');
  setRefreshBar(true, 3, '同步官方目录…');
  try {
    await apiPost('/api/refresh', {});
    const started = Date.now();
    while (Date.now() - started < 180000) {
      const s = await apiGet('/api/refresh/status');
      setRefreshBar(true, s.percent || 5, s.message || '同步中…');
      if (!s.running) {
        if (s.error) throw new Error(s.error);
        const r = s.result || {};
        toast(`目录已更新：${r.total || 0} 个版本，新增 ${r.added?.length || 0} 个`, 'ok');
        break;
      }
      await new Promise((res) => setTimeout(res, 700));
    }
    await reloadAll();
  } catch (err) {
    toast(`同步失败：${err.message}`, 'err');
  } finally {
    state.refreshing = false;
    btn.disabled = false;
    btn.querySelector('.ico')?.classList.remove('spin');
    setTimeout(() => setRefreshBar(false, 100), 600);
  }
}

/* ---------------- polling ---------------- */
function startPolling() {
  if (state.polling) return;
  state.polling = setInterval(async () => {
    const hasActive = state.jobs.some((j) => j.status === 'downloading');
    if (hasActive) {
      try {
        await refreshDownloads();
        const done = state.jobs.filter((j) => j.status === 'done');
        if (done.length) {
          for (const j of done) {
            toast(`${j.version} 安装包下载完成`, 'ok');
            state.jobs = state.jobs.filter((x) => x.id !== j.id);
          }
          await loadVersions();
        }
      } catch {}
    }
  }, 1200);
}

/* ---------------- events ---------------- */
function bindEvents() {
  document.addEventListener('click', async (e) => {
    const card = e.target.closest('[data-app-card]');
    if (card && !e.target.closest('button')) {
      await selectApp(card.dataset.appCard);
      return;
    }
    const sw = e.target.closest('[data-switch]');
    if (sw) {
      await selectApp(sw.dataset.switch);
      return;
    }
    const act = e.target.closest('[data-act]');
    if (act) {
      e.stopPropagation();
      const { act: name, version, app: appId, url, path, id } = act.dataset;
      try {
        if (name === 'detail') openDrawer(version);
        else if (name === 'download') await downloadVersion(version, appId || state.selectedAppId);
        else if (name === 'copy' || name === 'copy-url') {
          const text = name === 'copy-url' ? url : findVersion(version)?.winUrl;
          if (text) toast((await copyText(text)) ? '链接已复制' : '复制失败', 'ok');
        } else if (name === 'lock') await toggleLock(version, appId || state.selectedAppId);
        else if (name === 'unlock') await toggleLock(version || appById(appId)?.lock?.version, appId || state.selectedAppId);
        else if (name === 'lock-current') {
          const app = appById(appId);
          if (app?.install?.version) await toggleLock(app.install.version, appId);
        } else if (name === 'rollback') {
          const app = appById(appId);
          const cached = state.cached.some((c) => c.appId === appId && (c.versionShort === version || c.version === version));
          if (!cached) await downloadVersion(version, appId, true);
          installConfirm(version, appId);
        } else if (name === 'install') installConfirm(version, appId);
        else if (name === 'cancel-dl') {
          await apiPost(`/api/download/${encodeURIComponent(id)}/cancel`, {});
          await refreshDownloads();
        }         else if (name === 'reveal') {
          await apiPost('/api/reveal', { path });
        } else if (name === 'block-on' || name === 'block-off') {
          const res = await apiPost('/api/block', { enabled: name === 'block-on', restore: true });
          state.quarantine = res.quarantine;
          renderBlock();
          toast(name === 'block-on' ? '已拦截自动更新，更新包已移入隔离区' : '已解除拦截，更新包已恢复', 'ok');
        } else if (name === 'settings') {
          await loadState();
          settingsModal();
        }
      } catch (err) {
        toast(err.message, 'err');
      }
      return;
    }
    const selBtn = e.target.closest('[data-sel]');
    if (selBtn) {
      const kind = selBtn.dataset.sel;
      if (kind === 'clear') {
        state.selection.clear();
        renderVersions();
      } else if (kind === 'copy') {
        const text = selectedItems().map((it) => it.url).join('\r\n');
        toast((await copyText(text)) ? `已复制 ${state.selection.size} 条链接` : '复制失败', 'ok');
      } else if (kind === 'export') {
        exportModal(selectedItems());
      } else if (kind === 'download') {
        for (const v of state.selection) await downloadVersion(v, state.selectedAppId, true);
        toast(`已开始下载 ${state.selection.size} 个安装包`);
      } else if (kind === 'lock') {
        const v = [...state.selection][0];
        if (v) await toggleLock(v);
      }
      return;
    }
  });

  $('#versionRows').addEventListener('click', (e) => {
    if (e.target.closest('button') || e.target.closest('input')) return;
    const tr = e.target.closest('tr[data-version]');
    if (tr) openDrawer(tr.dataset.version);
  });

  document.addEventListener('change', (e) => {
    const chk = e.target.closest('[data-check]');
    if (chk) {
      const v = chk.dataset.check;
      if (chk.checked) state.selection.add(v);
      else state.selection.delete(v);
      renderVersions();
    }
  });

  $('#checkAll').addEventListener('change', (e) => {
    const rows = filteredVersions();
    if (e.target.checked) rows.forEach((r) => state.selection.add(r.version));
    else rows.forEach((r) => state.selection.delete(r.version));
    renderVersions();
  });

  $('#searchInput').addEventListener('input', (e) => {
    state.search = e.target.value;
    renderVersions();
  });
  $('#seriesSelect').addEventListener('change', (e) => {
    state.series = e.target.value;
    renderVersions();
  });
  $('#filterChips').addEventListener('click', (e) => {
    const chip = e.target.closest('.chip');
    if (!chip) return;
    state.filter = chip.dataset.filter;
    $$('#filterChips .chip').forEach((c) => c.classList.toggle('active', c === chip));
    renderVersions();
  });

  $('#drawerClose').addEventListener('click', closeDrawer);
  $('#drawerMask').addEventListener('click', closeDrawer);
  $('#modalClose').addEventListener('click', hideModal);
  $('#modalMask').addEventListener('click', (e) => {
    if (e.target.id === 'modalMask') hideModal();
  });
  $('#settingsBtn').addEventListener('click', async () => {
    await loadState();
    settingsModal();
  });
  $('#refreshBtn').addEventListener('click', doRefresh);
  $('#exportAllBtn').addEventListener('click', () => exportModal(filteredItems(), `wb-${state.selectedAppId}-packages`));
  $('#openStoreBtn').addEventListener('click', () => apiPost('/api/reveal', { path: state.storeDir }).catch((err) => toast(err.message, 'err')));

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      if (!$('#modalMask').hidden) hideModal();
      else if (!$('#drawer').hidden) closeDrawer();
    }
  });
}

/* ---------------- init ---------------- */
(async function init() {
  bindEvents();
  try {
    await loadState();
    await loadVersions();
    const deep = location.hash.match(/^#v?(\d+\.\d+\.\d+)$/);
    if (deep && findVersion(deep[1])) openDrawer(deep[1]);
    startPolling();
    setInterval(async () => {
      if (state.refreshing) return;
      try {
        const data = await apiGet('/api/state');
        const before = state.apps.map((a) => `${a.id}:${a.install?.version}:${a.lock?.version}:${a.drift}`).join('|');
        state.apps = data.apps;
        state.guard = data.guard;
        state.quarantine = data.quarantine;
        state.cached = data.cached;
        state.dataDirs = data.dataDirs;
        const after = state.apps.map((a) => `${a.id}:${a.install?.version}:${a.lock?.version}:${a.drift}`).join('|');
        if (before !== after) {
          renderAppCards();
          renderDrift();
          await loadVersions();
        }
      } catch {}
    }, 12000);
  } catch (err) {
    toast(`初始化失败：${err.message}`, 'err');
  }
})();
