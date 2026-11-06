// HTTP smoke test for the published server (run after `npm run sync-server`).
// It guards the routing/serialization layer that unit tests cannot reach: OTLP
// receivers must report failures instead of answering 200, unknown API paths
// must not fall through to the SPA, and a partial from/to must be rejected.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';

const serverRoot = path.join(import.meta.dirname, '..', 'server');
const binary = path.join(
  serverRoot,
  process.platform === 'win32' ? 'TokenTracker.Server.exe' : 'TokenTracker.Server',
);

if (!existsSync(binary)) {
  console.error(`server binary not found: ${binary}\nrun "npm run sync-server" first.`);
  process.exit(1);
}

const failures = [];
let checks = 0;

function check(name, actual, expected) {
  checks += 1;
  if (actual !== expected) {
    failures.push(`${name}: expected ${expected}, got ${actual}`);
  }
}

async function freePort() {
  return new Promise((resolve, reject) => {
    const probe = createServer();
    probe.unref();
    probe.on('error', reject);
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
}

async function request(url, init) {
  try {
    const response = await fetch(url, init);
    const body = await response.text();
    return { status: response.status, body, url };
  } catch (error) {
    return { status: 0, body: String(error), url };
  }
}

async function waitForHealth(base, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const probe = await request(`${base}/healthz`);
    if (probe.status === 200) {
      return true;
    }
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  return false;
}

function spanPayload(sessionId, count) {
  const now = Date.now();
  const spans = [];
  for (let index = 0; index < count; index += 1) {
    spans.push({
      traceId: String(index).padStart(32, '0'),
      spanId: String(index).padStart(16, '0'),
      name: 'chat smoke',
      kind: 1,
      startTimeUnixNano: `${now + index}000000`,
      endTimeUnixNano: `${now + index + 1}000000`,
      attributes: [
        { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
        { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
        // A chat span without token counts is dropped by the parser.
        { key: 'gen_ai.usage.input_tokens', value: { stringValue: '100' } },
        { key: 'gen_ai.usage.output_tokens', value: { stringValue: '10' } },
        {
          key: 'copilot_chat.session_id',
          value: { stringValue: sessionId },
        },
      ],
    });
  }

  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'copilot-chat' } },
          ],
        },
        scopeSpans: [{ spans }],
      },
    ],
  });
}

function metricsPayload(count) {
  const now = `${Date.now()}000000`;
  return JSON.stringify({
    resourceMetrics: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'copilot-chat' } },
          ],
        },
        scopeMetrics: [
          {
            metrics: [
              {
                name: 'copilot_chat.cloud.operation.count',
                sum: {
                  aggregationTemporality: 2,
                  isMonotonic: true,
                  dataPoints: [
                    {
                      asInt: String(count),
                      startTimeUnixNano: now,
                      timeUnixNano: now,
                    },
                  ],
                },
              },
            ],
          },
        ],
      },
    ],
  });
}

const port = await freePort();
const dataDir = mkdtempSync(path.join(tmpdir(), 'tokentracker-smoke-'));
const base = `http://127.0.0.1:${port}`;
const child = spawn(binary, [], {
  env: {
    ...process.env,
    TOKENTRACKER_PORT: String(port),
    TOKENTRACKER_DATA_DIR: dataDir,
    TOKENTRACKER_IDLE_EXIT_SECONDS: '0',
    TOKENTRACKER_BUILD_ID: 'smoke',
    TOKENTRACKER_CAPTURE_OTEL_METRICS: '',
  },
  stdio: 'ignore',
  windowsHide: true,
});

let exitCode = 0;
try {
  if (!(await waitForHealth(base, 25_000))) {
    throw new Error('server did not become healthy within 25s');
  }

  const badJson = {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{bad json',
  };

  check('GET /healthz', (await request(`${base}/healthz`)).status, 200);

  const version = await request(`${base}/api/version`);
  check('GET /api/version', version.status, 200);
  check(
    'GET /api/version name',
    JSON.parse(version.body).name,
    'TokenTracker.Server',
  );

  // Regression: these answered 200 because MapPost bound RequestDelegate and
  // discarded the returned IResult, so exporters never retried lost batches.
  check(
    'POST /v1/traces invalid JSON',
    (await request(`${base}/v1/traces`, badJson)).status,
    400,
  );
  check(
    'POST /v1/metrics invalid JSON',
    (await request(`${base}/v1/metrics`, badJson)).status,
    400,
  );
  check(
    'POST /otlp/copilot-chat/v1/traces invalid JSON',
    (await request(`${base}/otlp/copilot-chat/v1/traces`, badJson)).status,
    400,
  );
  check(
    'POST /otlp/agent-host/v1/metrics invalid JSON',
    (await request(`${base}/otlp/agent-host/v1/metrics`, badJson)).status,
    400,
  );
  check(
    'POST /otlp/unknown/v1/traces',
    (await request(`${base}/otlp/unknown/v1/traces`, badJson)).status,
    404,
  );

  // Regression: unknown API paths used to return index.html with status 200.
  check(
    'GET /api/unknown',
    (await request(`${base}/api/unknown`)).status,
    404,
  );
  check(
    'GET /otlp/unknown',
    (await request(`${base}/otlp/unknown`)).status,
    404,
  );

  const dashboard = await request(`${base}/api/dashboard?range=2`);
  check('GET /api/dashboard', dashboard.status, 200);
  check(
    'GET /api/dashboard payload',
    typeof JSON.parse(dashboard.body).outcomes,
    'object',
  );

  // A half-specified window used to be silently ignored.
  check(
    'GET /api/dashboard?from only',
    (await request(`${base}/api/dashboard?from=1700000000000`)).status,
    400,
  );
  check(
    'GET /api/dashboard?to only',
    (await request(`${base}/api/dashboard?to=1700000000000`)).status,
    400,
  );
  check(
    'GET /api/dashboard inverted range',
    (
      await request(
        `${base}/api/dashboard?from=1700000000000&to=1600000000000`,
      )
    ).status,
    400,
  );

  const ingest = await request(`${base}/v1/metrics`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: metricsPayload(3),
  });
  check('POST /v1/metrics valid', ingest.status, 200);
  const afterMetrics = JSON.parse(
    (await request(`${base}/api/dashboard?range=4`)).body,
  );
  check(
    'outcome metrics visible without restart',
    afterMetrics.outcomes.cloudOperations,
    3,
  );

  const system = await request(`${base}/api/system`);
  check('GET /api/system', system.status, 200);
  check(
    'GET /api/system metrics counter',
    JSON.parse(system.body).receiver.metricBatches,
    1,
  );

  // Model settings: the id is unique, prices and profile are optional and
  // invalid icon payloads must be refused instead of stored.
  const models = await request(`${base}/api/models`);
  check('GET /api/models', models.status, 200);
  check('GET /api/models payload', Array.isArray(JSON.parse(models.body)), true);

  const profile = await request(`${base}/api/models`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      model: 'test-model',
      provider: 'Test',
      iconKind: 'letter',
      iconLetter: 'T',
      iconColor: '#4D6BFE',
      priced: false,
      peakAware: false,
      cachedInput: 0,
      uncachedInput: 0,
      output: 0,
    }),
  });
  check('POST /api/models profile only', profile.status, 200);
  const savedProfile = JSON.parse(profile.body);
  check('POST /api/models keeps id', savedProfile.model, 'test-model');
  check('POST /api/models stores provider', savedProfile.provider, 'Test');
  check('POST /api/models unpriced', savedProfile.priced, false);

  check(
    'POST /api/models invalid icon kind',
    (
      await request(`${base}/api/models`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ model: 'bad', iconKind: 'emoji', priced: false }),
      })
    ).status,
    400,
  );
  check(
    'POST /api/models image without data',
    (
      await request(`${base}/api/models`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ model: 'bad', iconKind: 'image', priced: false }),
      })
    ).status,
    400,
  );

  const priced = await request(`${base}/api/models`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      model: 'test-model',
      provider: 'Test',
      iconKind: 'letter',
      iconLetter: 'T',
      iconColor: '#4D6BFE',
      priced: true,
      peakAware: false,
      cachedInput: 1,
      uncachedInput: 2,
      output: 3,
    }),
  });
  check('POST /api/models with price', priced.status, 200);
  const savedPrice = JSON.parse(priced.body);
  check('POST /api/models price roundtrip', savedPrice.cachedInput, 1);
  check('POST /api/models price flag', savedPrice.priced, true);

  const deleted = await fetch(
    `${base}/api/models?model=${encodeURIComponent('test-model')}`,
    { method: 'DELETE' },
  );
  check('DELETE /api/models', deleted.status, 204);
  const afterDelete = JSON.parse((await request(`${base}/api/models`)).body);
  check(
    'DELETE /api/models removes settings',
    afterDelete.some((row) => row.model === 'test-model'),
    false,
  );

  // Trace blocks are cursor-paged by start time (FIFO); the payload must expose
  // the cursor contract so the panel can append newer blocks.
  const sessionId = 'smoke-session';
  await request(`${base}/otlp/copilot-chat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: spanPayload(sessionId, 6),
  });
  const traceBlock = await request(
    `${base}/api/session/${sessionId}/trace?budget=1`,
  );
  check('GET /api/session/{id}/trace', traceBlock.status, 200);
  const block = JSON.parse(traceBlock.body);
  check('trace block returns nodes', Array.isArray(block.nodes), true);
  check('trace block is bounded by budget', block.nodes.length, 1);
  check('trace block reports hasMore', block.hasMore, true);
  check('trace block exposes a cursor', typeof block.nextCursor, 'string');
  check('trace block reports the total', block.total, 6);

  const older = JSON.parse(
    (
      await request(
        `${base}/api/session/${sessionId}/trace?budget=1&cursor=${encodeURIComponent(block.nextCursor)}`,
      )
    ).body,
  );
  check('trace cursor advances', older.nodes[0]?.id === block.nodes[0]?.id, false);
  check(
    'trace cursor keeps the remaining rows',
    older.hasMore,
    true,
  );
  check(
    'trace blocks stay FIFO',
    older.nodes[0].startTimeMs > block.nodes[0].startTimeMs,
    true,
  );

  const spa = await request(`${base}/`);
  check('GET /', spa.status, 200);
} catch (error) {
  failures.push(String(error));
} finally {
  child.kill();
  await new Promise((resolve) => {
    const timer = setTimeout(resolve, 3000);
    child.once('exit', () => {
      clearTimeout(timer);
      resolve();
    });
  });
  rmSync(dataDir, { recursive: true, force: true });
}

if (failures.length > 0) {
  console.error(`smoke failed (${failures.length}/${checks} checks):`);
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  exitCode = 1;
} else {
  console.log(`smoke passed (${checks} checks) on port ${port}`);
}

process.exit(exitCode);
