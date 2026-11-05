'use strict';

// Locale resolution is what decides whether a zh-tw user sees Traditional
// Chinese or falls back to English, so it is covered without the editor host.
const test = require('node:test');
const assert = require('node:assert');
const Module = require('node:module');
const path = require('node:path');

const vscodeStub = { env: { language: 'en' } };

const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'vscode') {
    return vscodeStub;
  }
  return originalLoad.call(this, request, parent, isMain);
};

const i18nPath = path.join(__dirname, '..', 'out', 'i18n.js');

function loadFor(language) {
  vscodeStub.env.language = language;
  delete require.cache[require.resolve(i18nPath)];
  return require(i18nPath);
}

test('resolves the VS Code language identifiers used for message catalogs', () => {
  const { resolveLocale } = loadFor('en');
  assert.strictEqual(resolveLocale('zh-cn'), 'zh-CN');
  assert.strictEqual(resolveLocale('zh-TW'), 'zh-TW');
  assert.strictEqual(resolveLocale('zh_Hant'), 'zh-TW');
  assert.strictEqual(resolveLocale('zh-hk'), 'zh-TW');
  assert.strictEqual(resolveLocale('en-gb'), 'en');
  // Unknown languages fall back to English instead of the Chinese source text.
  assert.strictEqual(resolveLocale('ja'), 'en');
  assert.strictEqual(resolveLocale(''), 'en');
});

test('serves simplified, traditional and English from the same call site', () => {
  assert.strictEqual(loadFor('zh-cn').tr('已连接', 'Connected'), '已连接');
  assert.strictEqual(loadFor('zh-tw').tr('已连接', 'Connected'), '已連線');
  assert.strictEqual(loadFor('en').tr('已连接', 'Connected'), 'Connected');
});

test('an explicit traditional string wins over the converter', () => {
  assert.strictEqual(
    loadFor('zh-tw').tr('设置', 'Settings', '偏好設定'),
    '偏好設定',
  );
});

test('modules built on tr() render Traditional Chinese under zh-tw', () => {
  loadFor('zh-tw');
  const moneyPath = path.join(__dirname, '..', 'out', 'money.js');
  delete require.cache[require.resolve(moneyPath)];
  const money = require(moneyPath);
  assert.strictEqual(money.unitName('credit'), '積分');
  assert.strictEqual(
    money.formatCost(1, 2, { unit: 'cny', usdRate: 7.2, creditRate: 0.01 }),
    '¥1.000000 + 2 次未定價',
  );
});
