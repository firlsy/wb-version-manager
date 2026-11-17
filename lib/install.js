import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import { appById, detectOne, isRunning } from './detect.js';
import { loadState, saveState, sameVersion } from './store.js';

function psQuote(value) {
  return `'${String(value).replace(/'/g, "''")}'`;
}

export async function killApp(appId) {
  const app = appById(appId);
  if (!app) throw new Error('未知应用');
  return await new Promise((resolve) => {
    let child;
    try {
      child = spawn('taskkill', ['/IM', app.process, '/F', '/T'], { windowsHide: true });
    } catch (err) {
      resolve({ code: -1, output: err.message });
      return;
    }
    let out = '';
    child.stdout?.on('data', (d) => (out += d.toString()));
    child.stderr?.on('data', (d) => (out += d.toString()));
    child.on('close', (code) => resolve({ code, output: out.trim() }));
    child.on('error', (err) => resolve({ code: -1, output: err.message }));
  });
}

export async function sha256File(file) {
  const hash = crypto.createHash('sha256');
  const fh = await fs.open(file, 'r');
  try {
    const stream = fh.createReadStream();
    for await (const chunk of stream) hash.update(chunk);
  } finally {
    await fh.close().catch(() => {});
  }
  return hash.digest('hex');
}

export async function dataSnapshot() {
  const home = process.env.USERPROFILE || '';
  const roaming = process.env.APPDATA || '';
  const local = process.env.LOCALAPPDATA || '';
  const items = [
    { label: '用户配置', path: path.join(roaming, 'WorkBuddy') },
    { label: '工作区与技能', path: path.join(home, '.workbuddy-ai') },
    { label: '更新器缓存', path: path.join(local, '@genieworkbuddy-desktop-updater') },
  ];
  const out = [];
  for (const item of items) {
    let exists = false;
    let mtime = '';
    try {
      const st = await fs.stat(item.path);
      exists = true;
      mtime = st.mtime.toISOString();
    } catch {}
    out.push({ ...item, exists, mtime });
  }
  return out;
}

/**
 * 用官方 NSIS 静默参数安装指定版本
 * 参数与客户端内置更新服务一致：/S --updated /UPDATE=1 /D=<安装目录>
 */
export async function installVersion({ appId, version, installerPath, sha256 = '' }) {
  const app = appById(appId);
  if (!app) throw new Error('未知应用');
  const state = await loadState();
  const info = await detectOne(appId, state.settings.customPaths);
  if (!info.found) throw new Error('未找到该应用的安装目录，请先在设置中指定');
  if (!installerPath) throw new Error('缺少安装包路径');
  const st = await fs.stat(installerPath).catch(() => null);
  if (!st) throw new Error('安装包不存在，请先下载');

  const running = await isRunning(app);
  if (running) await killApp(appId);

  const before = { version: info.version, installDir: info.installDir, at: new Date().toISOString() };
  const dataBefore = await dataSnapshot();

  let verify = null;
  if (sha256) {
    try {
      const actual = await sha256File(installerPath);
      verify = { expected: sha256.toLowerCase(), actual, ok: actual === sha256.toLowerCase() };
    } catch (err) {
      verify = { expected: sha256, actual: '', ok: false, error: err.message };
    }
    if (verify && !verify.ok) {
      throw new Error('安装包 SHA256 校验失败，已中止安装');
    }
  }

  const argsString = `/S --updated /UPDATE=1 /D=${info.installDir}`;
  const command = [
    `$ErrorActionPreference='Stop';`,
    `$p = Start-Process -FilePath ${psQuote(installerPath)} -ArgumentList ${psQuote(argsString)} -Verb RunAs -PassThru -Wait;`,
    `exit $p.ExitCode`,
  ].join(' ');

  const startedAt = Date.now();
  const result = await new Promise((resolve) => {
    let child;
    try {
      child = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], {
        windowsHide: true,
      });
    } catch (err) {
      resolve({ code: -1, stdout: '', stderr: err.message });
      return;
    }
    let stdout = '';
    let stderr = '';
    child.stdout?.on('data', (d) => (stdout += d.toString()));
    child.stderr?.on('data', (d) => (stderr += d.toString()));
    child.on('close', (code) => resolve({ code, stdout: stdout.trim(), stderr: stderr.trim() }));
    child.on('error', (err) => resolve({ code: -1, stdout: '', stderr: err.message }));
    setTimeout(() => {
      try {
        child.kill();
      } catch {}
    }, 10 * 60 * 1000);
  });

  // 安装器可能先返回，再等版本落盘
  let after = null;
  for (let i = 0; i < 30; i++) {
    await new Promise((r) => setTimeout(r, 2000));
    after = await detectOne(appId, state.settings.customPaths);
    if (after.version && after.version !== before.version) break;
    if (i > 5 && result.code === 0 && sameVersion(after.version, version)) break;
  }

  const ok = sameVersion(after?.version, version);
  const record = {
    appId,
    version,
    ok,
    at: new Date().toISOString(),
    durationMs: Date.now() - startedAt,
    before,
    after: after ? { version: after.version, installDir: after.installDir } : null,
    exitCode: result.code,
    stderr: result.stderr.slice(0, 2000),
    installerPath,
    verify,
    dataBefore,
  };
  await saveState({ lastInstall: record });
  return record;
}
