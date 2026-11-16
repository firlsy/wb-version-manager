import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const DATA_DIR = path.join(ROOT, 'data');
export const STORE_DIR = path.join(ROOT, 'store');
export const EXPORT_DIR = path.join(ROOT, 'exports');

export async function ensureDirs() {
  await Promise.all([
    fs.mkdir(DATA_DIR, { recursive: true }),
    fs.mkdir(STORE_DIR, { recursive: true }),
    fs.mkdir(EXPORT_DIR, { recursive: true }),
  ]);
}

export async function readJson(file, fallback = null) {
  try {
    const raw = await fs.readFile(file, 'utf8');
    return JSON.parse(raw);
  } catch {
    return fallback;
  }
}

export async function writeJson(file, data) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.tmp`;
  await fs.writeFile(tmp, JSON.stringify(data, null, 2), 'utf8');
  await fs.rename(tmp, file);
}

const STATE_FILE = path.join(DATA_DIR, 'state.json');

const DEFAULT_STATE = {
  locks: {},
  blocks: {},
  settings: {
    guard: false,
    autoRestore: false,
    customPaths: {},
    blockUpdates: false,
  },
  lastInstall: null,
  updatedAt: '',
};

let stateCache = null;

export async function loadState() {
  if (stateCache) return stateCache;
  const data = await readJson(STATE_FILE, null);
  stateCache = { ...structuredClone(DEFAULT_STATE), ...(data || {}) };
  stateCache.settings = { ...DEFAULT_STATE.settings, ...(data?.settings || {}) };
  stateCache.locks = data?.locks || {};
  return stateCache;
}

export async function saveState(patch = {}) {
  const state = await loadState();
  Object.assign(state, patch);
  state.updatedAt = new Date().toISOString();
  stateCache = state;
  await writeJson(STATE_FILE, state);
  return state;
}

export function formatBytes(n) {
  if (!Number.isFinite(n) || n <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function cmpVersion(a, b) {
  const pa = String(a).split('.').map((x) => parseInt(x, 10) || 0);
  const pb = String(b).split('.').map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}

export function seriesOf(version) {
  const parts = String(version).split('.');
  return parts.length >= 2 ? `${parts[0]}.${parts[1]}.x` : String(version);
}
