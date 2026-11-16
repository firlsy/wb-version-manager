import { spawn } from 'node:child_process';
import { loadState, saveState } from './store.js';

export const GUARD_TASK = 'WorkBuddyVersionLockGuard';

function run(cmd, args) {
  return new Promise((resolve) => {
    let child;
    try {
      child = spawn(cmd, args, { windowsHide: true });
    } catch (e) {
      resolve({ code: -1, stdout: '', stderr: e.message });
      return;
    }
    let out = '';
    let err = '';
    child.stdout?.on('data', (d) => (out += d.toString()));
    child.stderr?.on('data', (d) => (err += d.toString()));
    child.on('close', (code) => resolve({ code, stdout: out.trim(), stderr: err.trim() }));
    child.on('error', (e) => resolve({ code: -1, stdout: '', stderr: e.message }));
  });
}

export async function getLock(appId) {
  const state = await loadState();
  return state.locks[appId] || null;
}

export async function setLock(appId, version, extra = {}) {
  const state = await loadState();
  state.locks[appId] = {
    version,
    lockedAt: new Date().toISOString(),
    ...extra,
  };
  await saveState({ locks: state.locks });
  return state.locks[appId];
}

export async function clearLock(appId) {
  const state = await loadState();
  delete state.locks[appId];
  await saveState({ locks: state.locks });
}

let guardCache = { at: 0, value: null };

export async function guardStatus() {
  if (guardCache.value && Date.now() - guardCache.at < 15000) return guardCache.value;
  const res = await run('schtasks', ['/Query', '/TN', GUARD_TASK, '/FO', 'LIST']);
  const installed = res.code === 0;
  let nextRun = '';
  if (installed) {
    const m = res.stdout.match(/下次运行时间:\s*(.+)|Next Run Time:\s*(.+)/i);
    nextRun = (m && (m[1] || m[2]) || '').trim();
  }
  guardCache = { at: Date.now(), value: { installed, nextRun, taskName: GUARD_TASK } };
  return guardCache.value;
}

export async function setGuard(enabled, { nodePath, guardScript }) {
  if (!enabled) {
    const res = await run('schtasks', ['/Delete', '/TN', GUARD_TASK, '/F']);
    await saveState({ settings: { ...(await loadState()).settings, guard: false } });
    return { ok: res.code === 0 || /找不到|cannot find|does not exist/i.test(res.stderr + res.stdout), output: res.stdout || res.stderr };
  }
  const tr = `"${nodePath}" "${guardScript}" --fix`;
  const res = await run('schtasks', [
    '/Create',
    '/F',
    '/SC',
    'MINUTE',
    '/MO',
    '30',
    '/RL',
    'HIGHEST',
    '/TN',
    GUARD_TASK,
    '/TR',
    tr,
  ]);
  const ok = res.code === 0;
  await saveState({ settings: { ...(await loadState()).settings, guard: ok } });
  return { ok, output: (res.stdout || res.stderr).slice(0, 500) };
}

export async function runGuardNow({ guardScript, nodePath }) {
  return await run(nodePath, [guardScript, '--check']);
}
