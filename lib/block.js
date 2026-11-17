import fs from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

import { STORE_DIR, DATA_DIR, loadState, saveState, formatBytes } from './store.js';
import { APPS } from './detect.js';

const execFileAsync = promisify(execFile);

const LOCAL = process.env.LOCALAPPDATA || '';
const HOME = process.env.USERPROFILE || '';
const VAULT_DIR = path.join(LOCAL, 'wb-version-manager', 'quarantine');
const BACKUP_DIR = path.join(DATA_DIR, 'block-backup');

/* 旧版更新器把安装包暂存在这里（5.9.x 之前） */
const LEGACY_UPDATER_DIR = path.join(LOCAL, '@genieworkbuddy-desktop-updater');
const LEGACY_STAGED = path.join(LEGACY_UPDATER_DIR, 'installer.exe');

const INSTALLER_RE = /^WorkBuddy-Setup-(\d+(?:\.\d+)+)\.exe$/i;

function slugOf(name) {
  return String(name)
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/**
 * 锁定状态三态，和 C# 的 LockState 必须一字不差：
 *   active  = 配置层关闭 + 缓存目录写入阻断
 *   partial = 只生效了一层（用户自己在客户端关了更新，或 ACL 没加上）
 *   off     = 两层都没生效
 * 不看自己的 state.json 记录：记录会过期，配置和 ACL 才是本机事实。
 */
export const LOCK_OFF = 'off';
export const LOCK_PARTIAL = 'partial';
export const LOCK_ACTIVE = 'active';

export function evaluateLockState({ configDisabled = false, cacheBlocked = false } = {}) {
  if (configDisabled && cacheBlocked) return LOCK_ACTIVE;
  if (configDisabled || cacheBlocked) return LOCK_PARTIAL;
  return LOCK_OFF;
}

export function lockStateText(state) {
  if (state === LOCK_ACTIVE) return '已锁定';
  if (state === LOCK_PARTIAL) return '部分生效';
  return '未锁定';
}

/** 每个客户端的落点画像 */
export function profileOf(appId) {
  const app = APPS.find((a) => a.id === appId);
  if (!app) return null;
  const slug = slugOf(app.name);
  const runtimeDir = path.join(HOME, slug === 'workbuddy' ? '.workbuddy' : `.${slug}`);
  const tempRoot = path.join(LOCAL, 'Temp');
  return {
    appId,
    name: app.name,
    edition: app.edition,
    slug,
    runtimeDir,
    configFile: path.join(runtimeDir, 'app', 'app-config.json'),
    settingsFile: path.join(runtimeDir, 'settings.json'),
    failureFile: path.join(runtimeDir, 'app', 'win-install-failure.json'),
    attemptFile: path.join(runtimeDir, 'app', 'win-install-attempt.json'),
    lockSnapshotFile: path.join(runtimeDir, 'app', '.wbvm-block.json'),
    cacheDirs: [
      path.join(tempRoot, `workbuddy-update-x64-${slug}`),
      path.join(tempRoot, `workbuddy-update-arm64-${slug}`),
    ],
    storeDir: path.join(STORE_DIR, appId),
  };
}

async function statSafe(p) {
  try {
    return await fs.stat(p);
  } catch {
    return null;
  }
}

async function readJsonSafe(file) {
  try {
    return JSON.parse(await fs.readFile(file, 'utf8'));
  } catch {
    return null;
  }
}

async function writeJsonAtomic(file, data) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.wbvm.tmp`;
  await fs.writeFile(tmp, JSON.stringify(data, null, 2) + '\n', 'utf8');
  await fs.rename(tmp, file);
}

async function moveFile(src, dest) {
  try {
    await fs.rename(src, dest);
  } catch (err) {
    if (err.code !== 'EXDEV') throw err;
    await fs.copyFile(src, dest);
    await fs.unlink(src);
  }
}

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

/* ---------------- ACL 写入阻断 ---------------- */

function accountName() {
  const domain = process.env.USERDOMAIN;
  const user = process.env.USERNAME;
  if (!user) return '';
  return domain ? `${domain}\\${user}` : user;
}

async function icacls(args) {
  try {
    const { stdout } = await execFileAsync('icacls', args, { timeout: 8000, windowsHide: true });
    return { ok: true, out: stdout };
  } catch (err) {
    return { ok: false, out: `${err.stdout || ''}${err.stderr || ''}${err.message || ''}` };
  }
}

async function denyWrite(dir) {
  const who = accountName();
  if (!who) throw new Error('无法确定当前 Windows 账户');
  await fs.mkdir(dir, { recursive: true });
  // 只拒「写数据/追加/写属性」，保留目录枚举能力。
  // 用 (W) 会把 Synchronize / ReadControl 一起拒掉，工具自己都读不到缓存。
  const r = await icacls([dir, '/deny', `${who}:(WD,AD,WEA,WA)`]);
  if (!r.ok) throw new Error(`设置写入拒绝失败：${r.out.trim() || 'icacls 执行失败'}`);
}

async function allowWrite(dir) {
  const who = accountName();
  if (!who) return;
  const st = await statSafe(dir);
  if (!st?.isDirectory()) return;
  await icacls([dir, '/remove:d', who]);
}

const writableCache = new Map(); // dir -> {at, value}

async function probeWritable(dir) {
  const now = Date.now();
  const hit = writableCache.get(dir);
  if (hit && now - hit.at < 10000) return hit.value;
  const probe = path.join(dir, `.wbvm-probe-${process.pid}-${now}`);
  let value = true;
  try {
    await fs.writeFile(probe, 'x');
    await fs.unlink(probe).catch(() => {});
  } catch {
    value = false;
  }
  writableCache.set(dir, { at: now, value });
  return value;
}

/* ---------------- 状态 ---------------- */

async function cacheInfo(profile) {
  const dirs = [];
  for (const dir of profile.cacheDirs) {
    const st = await statSafe(dir);
    if (!st?.isDirectory()) {
      dirs.push({ path: dir, exists: false, writable: true, installers: [], total: 0, totalText: '0 B' });
      continue;
    }
    const installers = [];
    let total = 0;
    for (const name of await fs.readdir(dir).catch(() => [])) {
      const full = path.join(dir, name);
      const fst = await statSafe(full);
      if (!fst?.isFile()) continue;
      const m = INSTALLER_RE.exec(name);
      if (m) {
        total += fst.size;
        installers.push({
          name,
          version: m[1],
          size: fst.size,
          sizeText: formatBytes(fst.size),
          mtime: fst.mtime.toISOString(),
        });
      }
    }
    installers.sort((a, b) => b.mtime.localeCompare(a.mtime));
    dirs.push({
      path: dir,
      exists: true,
      writable: await probeWritable(dir),
      installers,
      total,
      totalText: formatBytes(total),
    });
  }
  const installers = dirs.flatMap((d) => d.installers);
  return {
    dirs,
    installers,
    count: installers.length,
    total: dirs.reduce((n, d) => n + d.total, 0),
    totalText: formatBytes(dirs.reduce((n, d) => n + d.total, 0)),
    blocked: dirs.some((d) => d.exists && d.writable === false),
  };
}

async function configInfo(profile) {
  const cfg = await readJsonSafe(profile.configFile);
  const settings = await readJsonSafe(profile.settingsFile);
  return {
    configFile: profile.configFile,
    configExists: cfg !== null,
    configValue: typeof cfg?.startupForceAutoUpdate === 'boolean' ? cfg.startupForceAutoUpdate : null,
    settingsFile: profile.settingsFile,
    settingsExists: settings !== null,
    settingsValue: typeof settings?.startupForceAutoUpdate === 'boolean' ? settings.startupForceAutoUpdate : null,
    disabled: cfg?.startupForceAutoUpdate === false || settings?.startupForceAutoUpdate === false,
  };
}

async function failureInfo(profile) {
  const rec = await readJsonSafe(profile.failureFile);
  return {
    exists: Boolean(rec),
    version: rec?.version || '',
    count: Number(rec?.failureCount || 0),
    code: rec?.lastCode || '',
    lastAt: rec?.lastFailureAt ? new Date(rec.lastFailureAt).toISOString() : '',
  };
}

async function legacyInfo() {
  const st = await statSafe(LEGACY_STAGED);
  const vault = [];
  for (const name of await fs.readdir(VAULT_DIR).catch(() => [])) {
    if (!/^installer_.*\.exe$/i.test(name)) continue;
    const fst = await statSafe(path.join(VAULT_DIR, name));
    if (fst) vault.push({ name, size: fst.size, sizeText: formatBytes(fst.size), mtime: fst.mtime.toISOString() });
  }
  vault.sort((a, b) => b.mtime.localeCompare(a.mtime));
  return {
    dir: LEGACY_UPDATER_DIR,
    staged: st ? { kind: st.isDirectory() ? 'placeholder' : 'file', sizeText: st.isDirectory() ? '' : formatBytes(st.size) } : { kind: 'none' },
    blocked: Boolean(st?.isDirectory()),
    vault,
  };
}

export async function blockStatus() {
  const state = await loadState();
  const apps = {};
  for (const app of APPS) {
    const profile = profileOf(app.id);
    const [cache, config, failure] = await Promise.all([cacheInfo(profile), configInfo(profile), failureInfo(profile)]);
    const record = state.blocks?.[app.id] || null;
    const lockState = evaluateLockState({ configDisabled: config.disabled, cacheBlocked: cache.blocked });
    apps[app.id] = {
      appId: app.id,
      name: app.name,
      edition: app.edition,
      runtimeDir: profile.runtimeDir,
      storeDir: profile.storeDir,
      cache,
      config,
      failure,
      state: lockState,
      enabled: lockState === LOCK_ACTIVE,
      partial: lockState === LOCK_PARTIAL,
      blockedAt: record?.at || '',
      legacy: await legacyInfo(),
    };
  }
  return {
    apps,
    anyEnabled: Object.values(apps).some((a) => a.state === LOCK_ACTIVE),
    vaultDir: VAULT_DIR,
    account: accountName(),
  };
}

/* ---------------- 开关 ---------------- */

async function recycleInstallers(profile) {
  const moved = [];
  await fs.mkdir(profile.storeDir, { recursive: true });
  for (const dir of profile.cacheDirs) {
    const st = await statSafe(dir);
    if (!st?.isDirectory()) continue;
    for (const name of await fs.readdir(dir).catch(() => [])) {
      const full = path.join(dir, name);
      const fst = await statSafe(full);
      if (!fst?.isFile()) continue;
      // 残留的半截下载
      if (/^WorkBuddy-Setup-.*\.exe\.tmp$/i.test(name)) {
        await fs.unlink(full).catch(() => {});
        continue;
      }
      const m = INSTALLER_RE.exec(name);
      if (!m) continue;
      const target = path.join(profile.storeDir, `WorkBuddy-win32-x64-user-${m[1]}-recycled.exe`);
      const exists = await statSafe(target);
      if (exists) {
        await fs.unlink(full).catch(() => {});
        moved.push({ version: m[1], target, deduped: true });
        continue;
      }
      await moveFile(full, target);
      moved.push({ version: m[1], target, sizeText: formatBytes(fst.size) });
    }
  }
  return moved;
}

async function backupFailureRecord(profile) {
  const cleared = [];
  for (const [file, label] of [
    [profile.failureFile, 'win-install-failure'],
    [profile.attemptFile, 'win-install-attempt'],
  ]) {
    const st = await statSafe(file);
    if (!st?.isFile()) continue;
    await fs.mkdir(BACKUP_DIR, { recursive: true });
    const dest = path.join(BACKUP_DIR, `${profile.appId}-${label}-${stamp()}.json`);
    await fs.copyFile(file, dest).catch(() => {});
    await fs.unlink(file).catch(() => {});
    cleared.push(label);
  }
  return cleared;
}

async function setStartupFlag(profile, value) {
  const prev = {};
  // app-config.json（早期静默更新用）
  const cfg = await readJsonSafe(profile.configFile);
  prev.config = { had: Boolean(cfg && Object.prototype.hasOwnProperty.call(cfg, 'startupForceAutoUpdate')), value: cfg?.startupForceAutoUpdate ?? null };
  const nextCfg = { ...(cfg || {}) };
  nextCfg.startupForceAutoUpdate = value;
  await writeJsonAtomic(profile.configFile, nextCfg);

  // settings.json（常规启动强制更新用）
  const settings = await readJsonSafe(profile.settingsFile);
  if (settings) {
    prev.settings = {
      had: Object.prototype.hasOwnProperty.call(settings, 'startupForceAutoUpdate'),
      value: settings.startupForceAutoUpdate ?? null,
    };
    settings.startupForceAutoUpdate = value;
    await writeJsonAtomic(profile.settingsFile, settings);
  } else {
    prev.settings = { had: false, value: null, skipped: true };
  }
  return prev;
}

async function restoreStartupFlag(profile, prev) {
  if (!prev) return;
  if (prev.config) {
    const existing = await readJsonSafe(profile.configFile);
    if (existing || prev.config.had) {
      const cfg = existing || {};
      if (prev.config.had) cfg.startupForceAutoUpdate = prev.config.value;
      else delete cfg.startupForceAutoUpdate;
      if (Object.keys(cfg).length) await writeJsonAtomic(profile.configFile, cfg);
    }
  }
  if (prev.settings && !prev.settings.skipped) {
    const existing = await readJsonSafe(profile.settingsFile);
    if (existing || prev.settings.had) {
      const settings = existing || {};
      if (prev.settings.had) settings.startupForceAutoUpdate = prev.settings.value;
      else delete settings.startupForceAutoUpdate;
      if (Object.keys(settings).length) await writeJsonAtomic(profile.settingsFile, settings);
    }
  }
}

/* ---------------- 锁定前快照（放在客户端配置目录，三个前端共享） ---------------- */

async function readSnapshot(profile) {
  const root = await readJsonSafe(profile.lockSnapshotFile);
  return root?.prev || null;
}

async function writeSnapshot(profile, prev) {
  if (!prev) return;
  await writeJsonAtomic(profile.lockSnapshotFile, {
    at: new Date().toISOString(),
    appId: profile.appId,
    prev,
  });
}

async function deleteSnapshot(profile) {
  await fs.unlink(profile.lockSnapshotFile).catch(() => {});
}

/** 第一次动这个客户端的配置前，把原件留一份 */
async function backupConfigFiles(profile) {
  const appDir = path.dirname(profile.configFile);
  await fs.mkdir(appDir, { recursive: true }).catch(() => {});
  await backupOne(profile.configFile, path.join(appDir, '.wbvm-backup-app-config.json'));
  await backupOne(profile.settingsFile, path.join(profile.runtimeDir, '.wbvm-backup-settings.json'));
}

async function backupOne(src, dest) {
  try {
    await fs.copyFile(src, dest);
  } catch {}
}

/** 解锁时收掉「上次锁定顺手建出来的空缓存目录」 */
async function removeCreatedDirs(record) {
  for (const dir of record?.createdDirs || []) {
    try {
      const entries = await fs.readdir(dir);
      if (!entries.length) await fs.rmdir(dir);
    } catch {}
  }
}

async function enableLegacyQuarantine() {
  const st = await statSafe(LEGACY_STAGED);
  if (st?.isDirectory()) return { changed: false };
  await fs.mkdir(LEGACY_UPDATER_DIR, { recursive: true });
  if (st?.isFile()) {
    await fs.mkdir(VAULT_DIR, { recursive: true });
    await moveFile(LEGACY_STAGED, path.join(VAULT_DIR, `installer_${stamp()}.exe`));
  }
  await fs.mkdir(LEGACY_STAGED, { recursive: true });
  return { changed: true };
}

async function disableLegacyQuarantine() {
  const st = await statSafe(LEGACY_STAGED);
  if (!st?.isDirectory()) return { changed: false };
  const children = await fs.readdir(LEGACY_STAGED).catch(() => []);
  if (children.length) return { changed: false, error: '占位目录非空，已跳过删除' };
  await fs.rmdir(LEGACY_STAGED);
  return { changed: true };
}

export async function enableBlock(appId) {
  const profile = profileOf(appId);
  if (!profile) throw new Error('未知应用');

  const moved = await recycleInstallers(profile);
  const cleared = await backupFailureRecord(profile);

  // 已经锁过一次时，当前配置里的 false 是我们自己写的，不能拿它覆盖原始值
  const state = await loadState();
  const old = state.blocks?.[appId] || null;
  const prev = old?.prev || (await readSnapshot(profile));
  if (!prev) await backupConfigFiles(profile);
  const current = await setStartupFlag(profile, false);
  const snapshot = prev || current;
  await writeSnapshot(profile, snapshot);

  // 每个架构的缓存目录都要挡：x64 仿真写 x64 目录，原生 ARM64 客户端写 arm64 目录
  const aclErrors = [];
  const createdDirs = [];
  for (const dir of profile.cacheDirs) {
    const existed = Boolean(await statSafe(dir));
    try {
      await denyWrite(dir);
      if (!existed) createdDirs.push(dir);
    } catch (err) {
      aclErrors.push(`${dir} → ${err.message}`);
    }
  }
  writableCache.clear();
  await enableLegacyQuarantine().catch(() => {});

  if (aclErrors.length) {
    // 没有 ACL 就等于没拦住下载。宁可报失败，也不要写一条 enabled 假装锁上了。
    throw new Error(
      `缓存目录写入阻断失败，拦截没有完成：\n${aclErrors.join('\n')}\n` +
        '启动开关已关闭、已下载的安装包已回收，但客户端仍可能把新版本下下来。'
    );
  }

  const blocks = { ...(state.blocks || {}) };
  blocks[appId] = { enabled: true, at: new Date().toISOString(), prev: snapshot, createdDirs, moved: moved.length, cleared };
  await saveState({ blocks, settings: { ...state.settings, blockUpdates: true } });

  return { ok: true, appId, moved, cleared, status: (await blockStatus()).apps[appId] };
}

export async function disableBlock(appId) {
  const profile = profileOf(appId);
  if (!profile) throw new Error('未知应用');
  const state = await loadState();
  const record = state.blocks?.[appId];

  for (const dir of profile.cacheDirs) await allowWrite(dir);
  writableCache.clear();

  // 撤掉拒绝项之后再探一次：还是写不进去就说明没解开，别报成功
  const stuck = [];
  for (const dir of profile.cacheDirs) {
    const st = await statSafe(dir);
    if (st?.isDirectory() && !(await probeWritable(dir))) stuck.push(dir);
  }
  if (stuck.length) throw new Error(`缓存目录仍不可写，解除没有完成：\n${stuck.join('\n')}`);

  await removeCreatedDirs(record);
  const prev = record?.prev || (await readSnapshot(profile));
  // 找不到原始值就不要动客户端配置：那个 false 可能是用户自己在客户端里设的
  if (prev) await restoreStartupFlag(profile, prev);
  await deleteSnapshot(profile);

  const blocks = { ...(state.blocks || {}) };
  delete blocks[appId];
  const anyLeft = Object.values(blocks).some((b) => b?.enabled);
  if (!anyLeft) await disableLegacyQuarantine().catch(() => {});
  await saveState({ blocks, settings: { ...state.settings, blockUpdates: anyLeft } });

  return { ok: true, appId, status: (await blockStatus()).apps[appId] };
}

/** 兼容旧接口：整体状态 */
export async function quarantineStatus() {
  return blockStatus();
}
