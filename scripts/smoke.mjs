// 纯函数自检：不碰真实 ACL、不写用户目录，只验证容易漂的那几处口径。
// 运行：npm test
import assert from 'node:assert/strict';

import { evaluateLockState, LOCK_ACTIVE, LOCK_PARTIAL, LOCK_OFF } from '../lib/block.js';
import { buildNameIndex, filenameFromUrl, parseVersionFromFilename } from '../lib/downloads.js';
import { shortVersion, sameVersion, cmpVersion } from '../lib/store.js';

let passed = 0;
let failed = 0;

function check(name, fn) {
  try {
    fn();
    passed++;
    console.log('  ok    ' + name);
  } catch (err) {
    failed++;
    console.error('  FAIL  ' + name + '\n        ' + err.message);
  }
}

console.log('\n锁定三态');
check('配置层 + ACL 都在 → active', () =>
  assert.equal(evaluateLockState({ configDisabled: true, cacheBlocked: true }), LOCK_ACTIVE));
check('只有配置层 → partial（用户自己关了更新也算）', () =>
  assert.equal(evaluateLockState({ configDisabled: true, cacheBlocked: false }), LOCK_PARTIAL));
check('只有 ACL → partial', () =>
  assert.equal(evaluateLockState({ configDisabled: false, cacheBlocked: true }), LOCK_PARTIAL));
check('两层都没有 → off', () => assert.equal(evaluateLockState({}), LOCK_OFF));

console.log('\n缓存包版本号解析');
check('4 段构建号', () =>
  assert.equal(parseVersionFromFilename('WorkBuddy-win32-x64-user-5.11.3.43806934-ecace0f7.exe'), '5.11.3.43806934'));
check('3 段回收包', () =>
  assert.equal(parseVersionFromFilename('WorkBuddy-win32-x64-user-5.11.4-recycled.exe'), '5.11.4'));
check('没有版本号', () => assert.equal(parseVersionFromFilename('installer.exe'), ''));
check('4 段的短版本是前 3 段', () => assert.equal(shortVersion('5.11.3.43806934'), '5.11.3'));

console.log('\n版本一致性');
check('3 段与 4 段视为同一版', () => assert.equal(sameVersion('5.11.3', '5.11.3.43806934'), true));
check('不同版本不相等', () => assert.equal(sameVersion('5.11.3', '5.11.4'), false));
check('空值不算相等', () => assert.equal(sameVersion('', '5.11.3'), false));
check('排序 5.11.4 > 5.11.3', () => assert.ok(cmpVersion('5.11.4', '5.11.3') > 0));

console.log('\n错版索引');
const catalog = {
  releases: [
    {
      version: '1.0.0',
      packages: {
        'win32-x64-user': { url: 'https://download.codebuddy.cn/a/WorkBuddy-win32-x64-user-1.0.0-aaaa1111.exe' },
      },
    },
    {
      version: '1.0.0',
      intlPackages: {
        'win32-x64-user': { url: 'https://download.codebuddy.ai/a/WorkBuddy-win32-x64-user-1.0.0-aaaa1111.exe' },
      },
    },
    {
      version: '0.9.0',
      packages: {
        'win32-x64-user': { url: 'https://download.codebuddy.cn/a/WorkBuddy-win32-x64-user-0.9.0-cccc3333.exe' },
      },
    },
    {
      version: '0.8.0',
      intlPackages: {
        'win32-x64-user': { url: 'https://download.codebuddy.ai/a/WorkBuddy-win32-x64-user-0.8.0-bbbb2222.exe' },
      },
    },
  ],
};
const index = buildNameIndex(catalog);
check('国内包归国内版', () =>
  assert.equal(index.get('workbuddy-win32-x64-user-0.9.0-cccc3333.exe'), 'workbuddy'));
check('国际包归国际版', () =>
  assert.equal(index.get('workbuddy-win32-x64-user-0.8.0-bbbb2222.exe'), 'workbuddyai'));
check('同名包记为 ambiguous 而不是瞎猜', () =>
  assert.equal(index.get('workbuddy-win32-x64-user-1.0.0-aaaa1111.exe'), 'ambiguous'));

console.log('\n下载文件名');
check('解码后的 ../ 被截掉', () =>
  assert.equal(filenameFromUrl('https://download.codebuddy.cn/a/%2e%2e%2fevil.exe'), 'evil.exe'));
check('反斜杠穿越被截掉', () =>
  assert.equal(filenameFromUrl('https://download.codebuddy.cn/a/b%5C..%5C..%5Cevil.exe'), 'evil.exe'));

console.log(`\n${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
