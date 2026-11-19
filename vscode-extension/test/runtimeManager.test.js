'use strict';

// Runtime cleanup decides whether an old server directory may be deleted. The
// dangerous case is a directory that a live server is still serving from: its
// executable is locked, but its wwwroot is not, so a naive recursive delete
// removes the dashboard assets from under the running process.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const { spawn } = require('node:child_process');
const Module = require('node:module');

// The compiled module pulls in i18n.js, which requires 'vscode'. Stub it the same
// way the status-bar tests do so the cleanup rules can run outside the editor.
const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'vscode') {
    return {
      env: { language: 'en' },
      workspace: { getConfiguration: () => ({ get: (_key, fallback) => fallback }) },
    };
  }
  return originalLoad.call(this, request, parent, isMain);
};

const {
  cleanupOldRuntimes,
  hasPreparedRuntime,
  resolveCachedRuntime,
} = require(path.join(__dirname, '..', 'out', 'runtimeManager.js'));

const executableName = process.platform === 'win32'
  ? 'TokenTracker.Server.exe'
  : 'TokenTracker.Server';

function tempStorage() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'tokentracker-runtime-'));
}

function makeRuntime(storage, buildId) {
  const directory = path.join(storage, 'runtime', buildId);
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(path.join(directory, executableName), 'placeholder');
  fs.mkdirSync(path.join(directory, 'wwwroot'));
  fs.writeFileSync(path.join(directory, 'wwwroot', 'index.html'), '<html></html>');
  return directory;
}

// A mapped executable is what blocks deletion in production; a copy of the
// current Node binary is a cheap stand-in that produces the same lock on every
// platform (Windows EBUSY, Linux ETXTBSY).
function startFakeServer(directory) {
  const executable = path.join(directory, executableName);
  fs.copyFileSync(process.execPath, executable);
  fs.mkdirSync(path.join(directory, 'wwwroot'), { recursive: true });
  fs.writeFileSync(
    path.join(directory, 'wwwroot', 'index.html'),
    '<html></html>',
  );
  return spawn(executable, ['-e', 'setTimeout(() => {}, 60000)'], {
    stdio: 'ignore',
  });
}

test('treats an existing runtime as not-first-run even without the state file', () => {
  const storage = tempStorage();
  try {
    assert.equal(hasPreparedRuntime(storage), false);

    // A ready runtime left by an older extension version must count too: the
    // state file alone would re-announce "first run" after an upgrade.
    const legacy = path.join(storage, 'runtime', 'legacy-build');
    fs.mkdirSync(legacy, { recursive: true });
    fs.writeFileSync(path.join(legacy, '.ready'), 'x');
    assert.equal(hasPreparedRuntime(storage), true);

    fs.writeFileSync(
      path.join(storage, 'server-runtime.json'),
      JSON.stringify({ buildId: 'legacy-build' }),
    );
    assert.equal(hasPreparedRuntime(storage), true);
  } finally {
    fs.rmSync(storage, { recursive: true, force: true });
  }
});

test('reuses the cached runtime without network access', () => {
  const storage = tempStorage();
  try {
    assert.equal(resolveCachedRuntime(storage), null);

    const directory = makeRuntime(storage, 'cached-build');
    fs.writeFileSync(path.join(directory, '.ready'), 'x');
    fs.writeFileSync(
      path.join(storage, 'server-runtime.json'),
      JSON.stringify({
        rid: 'win-x64',
        buildId: 'cached-build',
        serverVersion: '1.0.30',
        runtimeDirectory: directory,
        assetName: 'server.zip',
        releaseTag: 'server-v1.0.30',
      }),
    );

    const cached = resolveCachedRuntime(storage);
    assert.ok(cached);
    assert.equal(cached.buildId, 'cached-build');
    assert.equal(cached.serverVersion, '1.0.30');
    assert.equal(
      cached.executablePath,
      path.join(directory, executableName),
    );
    assert.equal(cached.dataDirectory, path.join(storage, 'data'));

    // A half-written runtime (no .ready) must not be reused.
    fs.rmSync(path.join(directory, '.ready'));
    assert.equal(resolveCachedRuntime(storage), null);
  } finally {
    fs.rmSync(storage, { recursive: true, force: true });
  }
});

test('removes stale runtimes and keeps the requested build', () => {  const storage = tempStorage();  try {
    makeRuntime(storage, 'old-build');
    const kept = makeRuntime(storage, 'new-build');

    const result = cleanupOldRuntimes(storage, 'new-build');

    assert.deepEqual(result.removed, ['old-build']);
    assert.deepEqual(result.skipped, []);
    assert.ok(fs.existsSync(kept));
    assert.ok(!fs.existsSync(path.join(storage, 'runtime', 'old-build')));
  } finally {
    fs.rmSync(storage, { recursive: true, force: true });
  }
});

test('never deletes a runtime whose executable is still running', async () => {
  const storage = tempStorage();
  const runningDirectory = path.join(storage, 'runtime', 'running-build');
  fs.mkdirSync(runningDirectory, { recursive: true });
  makeRuntime(storage, 'new-build');

  const child = startFakeServer(runningDirectory);
  try {
    // Give the image time to be mapped before probing it.
    await new Promise((resolve) => setTimeout(resolve, 500));

    const result = cleanupOldRuntimes(storage, 'new-build');

    assert.deepEqual(result.skipped, ['running-build']);
    assert.deepEqual(result.removed, []);
    assert.ok(
      fs.existsSync(path.join(runningDirectory, 'wwwroot', 'index.html')),
    );
  } finally {
    child.kill();
    await once(child, 'exit');
    fs.rmSync(storage, { recursive: true, force: true });
  }
});

test('removes the runtime again once its server exits', async () => {
  const storage = tempStorage();
  const directory = path.join(storage, 'runtime', 'exiting-build');
  fs.mkdirSync(directory, { recursive: true });
  makeRuntime(storage, 'new-build');

  const child = startFakeServer(directory);
  await new Promise((resolve) => setTimeout(resolve, 500));
  child.kill();
  await once(child, 'exit');

  try {
    const result = cleanupOldRuntimes(storage, 'new-build');
    assert.deepEqual(result.removed, ['exiting-build']);
    assert.ok(!fs.existsSync(directory));
  } finally {
    fs.rmSync(storage, { recursive: true, force: true });
  }
});
