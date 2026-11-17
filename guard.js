#!/usr/bin/env node
// 版本守护进程：检查锁定版本是否被自动更新覆盖，可选自动恢复
// 用法：node guard.js --check | --fix
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { APPS, detectApps } from './lib/detect.js';
import { loadState, saveState, sameVersion } from './lib/store.js';
import { loadCatalog } from './lib/catalog.js';
import { findCached, buildNameIndex } from './lib/downloads.js';
import { installVersion } from './lib/install.js';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const LOG = path.join(ROOT, 'data', 'guard.log');
const args = process.argv.slice(2);
const FIX = args.includes('--fix');

async function log(line) {
  const text = `[${new Date().toISOString()}] ${line}\n`;
  await fs.mkdir(path.dirname(LOG), { recursive: true });
  await fs.appendFile(LOG, text, 'utf8').catch(() => {});
}

async function main() {
  const state = await loadState();
  const detected = await detectApps(state.settings.customPaths);
  // 和网页版同一套错版判定：自动恢复也不能装另一版的包
  const nameIndex = buildNameIndex(await loadCatalog());
  const events = [];
  for (const app of APPS) {
    const lock = state.locks[app.id];
    if (!lock) continue;
    const info = detected[app.id];
    if (!info?.found) continue;
    const drift = info.version && !sameVersion(info.version, lock.version);
    events.push({ appId: app.id, locked: lock.version, current: info.version, drift });
    if (!drift) continue;
    await log(`漂移：${app.name} 当前 ${info.version}，锁定 ${lock.version}`);
    if (!FIX) continue;
    const cached = await findCached(app.id, lock.version, nameIndex);
    if (!cached) {
      await log(`无法恢复：缺少 ${lock.version} 安装包，请在工具内下载后重试`);
      continue;
    }
    const result = await installVersion({ appId: app.id, version: lock.version, installerPath: cached.path });
    await log(`恢复 ${lock.version} → ${result.ok ? '成功' : '失败'}（exit ${result.exitCode}）`);
  }
  const prev = state.guardEvents || [];
  await saveState({
    guardEvents: [...prev.slice(-49), { at: new Date().toISOString(), fix: FIX, events }],
    lastGuardAt: new Date().toISOString(),
  });
  console.log(JSON.stringify({ at: new Date().toISOString(), fix: FIX, events }, null, 2));
}

main().catch(async (err) => {
  await log(`守护异常：${err.message}`);
  console.error(err);
  process.exit(1);
});
