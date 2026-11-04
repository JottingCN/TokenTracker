'use strict';

// Node's built-in runner keeps the extension free of a test dependency.
// The compiled module requires 'vscode', which only exists inside the editor
// host, so it is stubbed before loading.
const test = require('node:test');
const assert = require('node:assert');
const Module = require('node:module');

class MarkdownString {
  constructor(value = '') {
    this.value = value;
  }
  appendMarkdown(text) {
    this.value += text;
    return this;
  }
  appendCodeblock(code) {
    this.value += '\n```text\n' + code + '\n```\n';
    return this;
  }
}

class Disposable {
  dispose() {}
}

const statusBarItem = {
  name: '',
  text: '',
  tooltip: '',
  command: '',
  visible: false,
  show() {
    this.visible = true;
  },
  hide() {
    this.visible = false;
  },
  dispose() {},
  onDidChangeVisibility: () => new Disposable(),
};

const vscodeStub = {
  MarkdownString,
  Disposable,
  StatusBarAlignment: { Left: 1, Right: 2 },
  env: { language: 'zh-cn' },
  window: { createStatusBarItem: () => statusBarItem },
  workspace: { getConfiguration: () => ({ get: (_key, fallback) => fallback }) },
};

const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'vscode') {
    return vscodeStub;
  }
  return originalLoad.call(this, request, parent, isMain);
};

const path = require('node:path');
const {
  selectLatestSession,
  sessionName,
  truncateForTip,
  StatusBarController,
} = require(path.join(__dirname, '..', 'out', 'statusBar.js'));

function session(id, workspacePath, lastSeenMs, overrides = {}) {
  return {
    sessionId: id,
    sessionDisplay: id,
    workspacePath,
    lastSeenMs,
    hitRate: 0.5,
    costCny: 0.1,
    unpricedCalls: 0,
    inputTokens: 100,
    outputTokens: 10,
    cacheReadTokens: 50,
    ...overrides,
  };
}

const root = 'd:\\source\\project-a';

test('matches only sessions inside the current workspace', () => {
  const sessions = [
    session('a', 'd:\\source\\project-a\\sub', 30),
    session('b', 'd:\\source\\project-b', 40),
  ];
  const result = selectLatestSession(sessions, [root]);
  assert.equal(result.session.sessionId, 'a');
});

test('picks the most recent matching session', () => {
  const sessions = [
    session('old', 'd:\\source\\project-a', 10),
    session('new', 'd:\\source\\project-a', 20),
  ];
  assert.equal(selectLatestSession(sessions, [root]).session.sessionId, 'new');
});

test('a workspace window ignores unattributed sessions', () => {
  const sessions = [
    session('other', 'd:\\source\\project-b', 50),
    session('unknown-1', null, 10),
    session('unknown-2', undefined, 30),
  ];
  const result = selectLatestSession(sessions, [root]);
  assert.equal(result.session, undefined);
});

test('never shows another workspace when every session is assigned', () => {
  const sessions = [session('other', 'd:\\source\\project-b', 50)];
  const result = selectLatestSession(sessions, [root]);
  assert.equal(result.session, undefined);
});

test('uses all sessions when no folder is open', () => {
  const sessions = [session('a', 'd:\\source\\project-a', 10), session('b', null, 20)];
  assert.equal(selectLatestSession(sessions, []).session.sessionId, 'b');
});

test('reports AHP connection changes so watchers can pause', () => {
  const seen = [];
  const controller = new StatusBarController(
    0,
    4,
    '全部',
    [],
    { unit: 'cny', usdRate: 7.2, creditRate: 0.01 },
    '$(pulse) {hit}',
    () => {},
    (connected) => seen.push(connected),
  );

  const summary = session('session-1', null, 20, {
    firstSeenMs: 20,
    inputText: '1,000',
    cacheText: '500',
    outputText: '10',
    reasoningText: '0',
    uncachedCostCny: 0.01,
    cacheCostCny: 0.001,
    outputCostCny: 0.002,
    reasoningCostCny: 0,
    hitRateText: '50%',
    avgTtftText: '-',
    tokensPerSecondText: '-',
    calls: 1,
    events: 0,
  });

  const payload = (connected) => ({
    summary: { hitRate: 0.5, costCny: 0.01 },
    sessions: [summary],
    ahp: { connected },
  });

  // TypeScript-only privacy: the compiled members stay reachable at runtime.
  controller.render(payload(true));
  controller.render(payload(true));
  controller.render(payload(false));

  assert.deepEqual(seen, [true, false]);
  controller.dispose();
});

test('tooltip carries no workspace attribution text', () => {
  const controller = new StatusBarController(
    0,
    4,
    '全部',
    [root],
    { unit: 'cny', usdRate: 7.2, creditRate: 0.01 },
    '$(pulse) {hit}',
    () => {},
  );

  const summary = session('session-1', root, 20, {
    title: '会话标题',
    firstSeenMs: 20,
    inputText: '1,000',
    cacheText: '500',
    outputText: '10',
    reasoningText: '0',
    uncachedCostCny: 0.01,
    cacheCostCny: 0.001,
    outputCostCny: 0.002,
    reasoningCostCny: 0,
    hitRateText: '50%',
    avgTtftText: '-',
    tokensPerSecondText: '-',
    calls: 1,
    events: 0,
  });

  // TypeScript-only privacy: the compiled members stay reachable at runtime.
  controller.updateTooltip(summary, {
    models: [],
    tools: [],
    hooks: [],
    agentRuns: [],
    events: [],
    calls: [],
  });

  const tooltip = statusBarItem.tooltip.value;
  assert.doesNotMatch(tooltip, /工作区/);
  assert.match(tooltip, /\*\*Token Tracker · 会话标题\*\*/);
  assert.match(tooltip, /\$\(calendar\) 全部 · \$\(clock\)/);
  controller.dispose();
});

test('names an untitled session with the localized fallback', () => {
  assert.equal(
    sessionName({ sessionId: 'e2940513-38f5-45a0-bd49-57c2a13071ab' }),
    `未命名 · ${'e2940513'}…`,
  );
  assert.equal(
    sessionName({ sessionId: 'e2940513-38f5-45a0', title: '  已命名  ' }),
    '已命名',
  );
});

test('clips long tool names so the tip cannot overflow', () => {
  assert.equal(truncateForTip('edit_File', 48), 'edit_File');
  assert.equal(truncateForTip('a'.repeat(48), 48), `${'a'.repeat(48)}`);
  assert.equal(
    truncateForTip('a'.repeat(80), 48),
    `${'a'.repeat(47)}…`,
  );
  // Wide characters consume two columns each; the ellipsis stays inside the budget.
  assert.equal(
    truncateForTip('中'.repeat(60), 48),
    `${'中'.repeat(23)}…`,
  );
});
