'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('sync-server reads the version from the server API declaration', async () => {
  const { readServerVersion } = await import('../scripts/server-version.mjs');
  const programPath = path.join(
    __dirname,
    '..',
    '..',
    'src',
    'TokenTracker.Server',
    'Program.cs',
  );
  const source = fs.readFileSync(programPath, 'utf8');

  assert.match(readServerVersion(source), /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/);
  assert.throws(() => readServerVersion('var serverInfo = new ServerInfoPayload();'));
});
