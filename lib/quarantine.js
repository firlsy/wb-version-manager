import fs from 'node:fs/promises';
import path from 'node:path';
import { loadState, saveState, formatBytes } from './store.js';

const UPDATER_DIR = path.join(process.env.LOCALAPPDATA || '', '@genieworkbuddy-desktop-updater');
const STAGED_NAME = 'installer.exe';
const STAGED_PATH = path.join(UPDATER_DIR, STAGED_NAME);
const VAULT_DIR = path.join(process.env.LOCALAPPDATA || '', 'wb-version-manager', 'quarantine');

async function statSafe(p) {
  try {
    return await fs.stat(p);
  } catch {
    return null;
  }
}

export async function quarantineStatus() {
  const state = await loadState();
  const dirStat = await statSafe(UPDATER_DIR);
  const stagedStat = await statSafe(STAGED_PATH);
  let staged = { kind: 'none' };
  if (stagedStat) {
    staged = {
      kind: stagedStat.isDirectory() ? 'placeholder' : 'file',
      size: stagedStat.size,
      sizeText: stagedStat.isDirectory() ? '' : formatBytes(stagedStat.size),
      mtime: stagedStat.mtime.toISOString(),
    };
  }
  let vault = [];
  try {
    const files = await fs.readdir(VAULT_DIR);
    for (const name of files.filter((f) => /^installer_.*\.exe$/i.test(f))) {
      const st = await statSafe(path.join(VAULT_DIR, name));
      if (st) vault.push({ name, size: st.size, sizeText: formatBytes(st.size), mtime: st.mtime.toISOString() });
    }
  } catch {}
  vault.sort((a, b) => b.mtime.localeCompare(a.mtime));
  return {
    updaterDir: UPDATER_DIR,
    exists: Boolean(dirStat),
    staged,
    locked: staged.kind === 'placeholder',
    enabled: Boolean(state.settings?.blockUpdates),
    vaultDir: VAULT_DIR,
    vault,
  };
}

export async function enableBlock() {
  const status = await quarantineStatus();
  if (status.locked) {
    await saveState({ settings: { ...(await loadState()).settings, blockUpdates: true } });
    return { ...(await quarantineStatus()), changed: false };
  }
  await fs.mkdir(UPDATER_DIR, { recursive: true });
  const staged = await statSafe(STAGED_PATH);
  if (staged?.isFile()) {
    await fs.mkdir(VAULT_DIR, { recursive: true });
    const d = new Date();
    const p2 = (n) => String(n).padStart(2, '0');
    const stamp = `${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}_${p2(d.getHours())}${p2(d.getMinutes())}${p2(d.getSeconds())}`;
    const target = path.join(VAULT_DIR, `installer_${stamp}.exe`);
    try {
      await fs.rename(STAGED_PATH, target);
    } catch (err) {
      if (err.code === 'EXDEV') {
        await fs.copyFile(STAGED_PATH, target);
        await fs.unlink(STAGED_PATH);
      } else {
        throw new Error(`无法移动已下载的更新包（可能客户端正在运行）：${err.message}`);
      }
    }
  }
  await fs.mkdir(STAGED_PATH, { recursive: true });
  await saveState({ settings: { ...(await loadState()).settings, blockUpdates: true } });
  return { ...(await quarantineStatus()), changed: true };
}

export async function disableBlock({ restore = true } = {}) {
  const status = await quarantineStatus();
  if (status.staged.kind === 'placeholder') {
    const children = await fs.readdir(STAGED_PATH).catch(() => []);
    if (children.length) {
      throw new Error('占位目录非空，已跳过删除。请手动检查后重试。');
    }
    await fs.rmdir(STAGED_PATH);
  }
  let restored = '';
  if (restore) {
    const latest = status.vault[0];
    if (latest) {
      await fs.mkdir(UPDATER_DIR, { recursive: true });
      await fs.rename(path.join(VAULT_DIR, latest.name), STAGED_PATH).catch(async (err) => {
        if (err.code === 'EXDEV') {
          await fs.copyFile(path.join(VAULT_DIR, latest.name), STAGED_PATH);
          await fs.unlink(path.join(VAULT_DIR, latest.name));
        } else throw err;
      });
      restored = latest.name;
    }
  }
  await saveState({ settings: { ...(await loadState()).settings, blockUpdates: false } });
  return { ...(await quarantineStatus()), restored };
}
