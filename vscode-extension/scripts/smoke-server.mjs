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

// Hook signal arrives as OTel span events on the turn's invoke_agent span (there is
// no execute_hook span in either pipeline). Only hook.end must become a row.
function hookPayload(sessionId) {
  const now = Date.now();
  const traceId = 'ababababababababababababababab01';
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
            { key: 'service.namespace', value: { stringValue: 'vscode.agent-host' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              {
                traceId,
                spanId: 'turn-hook-0000001',
                parentSpanId: 'host-session-00000',
                name: 'invoke_agent explore',
                kind: 1,
                startTimeUnixNano: `${now + 10}000000`,
                endTimeUnixNano: `${now + 900}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
                events: [
                  {
                    name: 'github.copilot.hook.start',
                    timeUnixNano: `${now + 20}000000`,
                    attributes: [
                      { key: 'github.copilot.hook.type', value: { stringValue: 'preToolUse' } },
                      { key: 'github.copilot.hook.invocation_id', value: { stringValue: 'hook-1' } },
                    ],
                  },
                  {
                    name: 'github.copilot.hook.end',
                    timeUnixNano: `${now + 30}000000`,
                    attributes: [
                      { key: 'github.copilot.hook.type', value: { stringValue: 'preToolUse' } },
                      { key: 'github.copilot.hook.invocation_id', value: { stringValue: 'hook-1' } },
                      { key: 'github.copilot.hook.duration', value: { doubleValue: 0.25 } },
                    ],
                  },
                  {
                    name: 'github.copilot.hook.end',
                    timeUnixNano: `${now + 40}000000`,
                    attributes: [
                      { key: 'github.copilot.hook.type', value: { stringValue: 'postToolUse' } },
                      { key: 'github.copilot.hook.invocation_id', value: { stringValue: 'hook-2' } },
                      { key: 'github.copilot.hook.duration', value: { doubleValue: 0.0005 } },
                    ],
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
  });
}

// A turn with a named main agent, one model call and a subagent run below an
// execute_tool span. The invoke_agent spans carry no usage, like the real host.
function agentTurnPayload(sessionId) {
  const now = Date.now();
  const traceId = 'acacacacacacacacacacacacacacac01';
  const resource = {
    attributes: [
      { key: 'service.name', value: { stringValue: 'github-copilot' } },
      { key: 'service.namespace', value: { stringValue: 'vscode.agent-host' } },
    ],
  };
  const turn = {
    traceId,
    spanId: 'agent-turn-0001',
    parentSpanId: 'host-session-00000',
    name: 'invoke_agent main-agent',
    kind: 1,
    startTimeUnixNano: `${now + 1000}000000`,
    endTimeUnixNano: `${now + 9000}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  };
  const chat = {
    traceId,
    spanId: 'agent-chat-0001',
    parentSpanId: 'agent-turn-0001',
    name: 'chat smoke-model',
    kind: 1,
    startTimeUnixNano: `${now + 1100}000000`,
    endTimeUnixNano: `${now + 2100}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
      { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
      { key: 'gen_ai.usage.input_tokens', value: { intValue: '100' } },
      { key: 'gen_ai.usage.cache_read.input_tokens', value: { intValue: '80' } },
      { key: 'gen_ai.usage.output_tokens', value: { intValue: '10' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  };
  const task = {
    traceId,
    spanId: 'agent-task-0001',
    parentSpanId: 'agent-turn-0001',
    name: 'execute_tool task',
    kind: 1,
    startTimeUnixNano: `${now + 3000}000000`,
    endTimeUnixNano: `${now + 8000}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'execute_tool' } },
      { key: 'gen_ai.tool.name', value: { stringValue: 'task' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  };
  const sub = {
    traceId,
    spanId: 'agent-sub-0001',
    parentSpanId: 'agent-task-0001',
    name: 'invoke_agent explore',
    kind: 1,
    startTimeUnixNano: `${now + 4000}000000`,
    endTimeUnixNano: `${now + 7000}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  };
  const subChat = {
    traceId,
    spanId: 'agent-subchat-001',
    parentSpanId: 'agent-sub-0001',
    name: 'chat smoke-model',
    kind: 1,
    startTimeUnixNano: `${now + 4500}000000`,
    endTimeUnixNano: `${now + 5500}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
      { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
      { key: 'gen_ai.usage.input_tokens', value: { intValue: '200' } },
      { key: 'gen_ai.usage.cache_read.input_tokens', value: { intValue: '120' } },
      { key: 'gen_ai.usage.output_tokens', value: { intValue: '20' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  };
  return JSON.stringify({
    resourceSpans: [
      {
        resource,
        scopeSpans: [
          { spans: [turn, chat, task, sub, subChat] },
        ],
      },
    ],
  });
}

function turnPayload(sessionId) {
  const now = Date.now();
  const traceId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              {
                traceId,
                spanId: 'turn-root-0000001',
                name: 'invoke_agent',
                kind: 1,
                startTimeUnixNano: `${now}000000`,
                endTimeUnixNano: `${now + 1000}000000`,
                attributes: [
                  {
                    key: 'gen_ai.operation.name',
                    value: { stringValue: 'invoke_agent' },
                  },
                  {
                    key: 'gen_ai.conversation.id',
                    value: { stringValue: sessionId },
                  },
                ],
              },
              {
                traceId,
                spanId: 'perm-span-000001',
                parentSpanId: 'turn-root-0000001',
                name: 'permission',
                kind: 1,
                startTimeUnixNano: `${now + 10}000000`,
                endTimeUnixNano: `${now + 1010}000000`,
                attributes: [
                  {
                    key: 'github.copilot.permission.kind',
                    value: { stringValue: 'shell' },
                  },
                  {
                    key: 'github.copilot.permission.result',
                    value: { stringValue: 'approved' },
                  },
                  {
                    key: 'github.copilot.permission.tool_name',
                    value: { stringValue: 'powershell' },
                  },
                  {
                    key: 'gen_ai.conversation.id',
                    value: { stringValue: sessionId },
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
  });
}

// A turn plus a tool and its approval, where the permission span carries no
// tool_name (which is what the host actually sends).
function approvalWithoutToolNamePayload(sessionId) {
  const now = Date.now();
  const traceId = 'ffffffffffffffffffffffffffffffff';
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              {
                traceId,
                spanId: 'turn-approval-0001',
                name: 'invoke_agent',
                kind: 1,
                startTimeUnixNano: `${now}000000`,
                endTimeUnixNano: `${now + 500}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
              {
                traceId,
                spanId: 'chat-approval-0001',
                parentSpanId: 'turn-approval-0001',
                name: 'chat smoke-model',
                kind: 1,
                startTimeUnixNano: `${now + 10}000000`,
                endTimeUnixNano: `${now + 20}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
                  { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
                  { key: 'gen_ai.usage.input_tokens', value: { stringValue: '100' } },
                  { key: 'gen_ai.usage.output_tokens', value: { stringValue: '10' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
              {
                traceId,
                spanId: 'tool-approval-0001',
                parentSpanId: 'turn-approval-0001',
                name: 'execute_tool grep',
                kind: 1,
                startTimeUnixNano: `${now + 30}000000`,
                endTimeUnixNano: `${now + 130}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'execute_tool' } },
                  { key: 'gen_ai.tool.name', value: { stringValue: 'grep' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
              {
                traceId,
                spanId: 'perm-approval-0001',
                parentSpanId: 'tool-approval-0001',
                name: 'permission',
                kind: 1,
                startTimeUnixNano: `${now + 40}000000`,
                endTimeUnixNano: `${now + 140}000000`,
                attributes: [
                  { key: 'github.copilot.permission.kind', value: { stringValue: 'read' } },
                  { key: 'github.copilot.permission.result', value: { stringValue: 'approved' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
            ],
          },
        ],
      },
    ],
  });
}

function orphanToolPayload(sessionId) {
  const now = Date.now();
  const traceId = 'abababababababababababababababab';
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              {
                traceId,
                spanId: 'turn-orphan-0001',
                name: 'invoke_agent',
                kind: 1,
                startTimeUnixNano: `${now}000000`,
                endTimeUnixNano: `${now + 500}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
              {
                traceId,
                spanId: 'chat-orphan-0001',
                parentSpanId: 'turn-orphan-0001',
                name: 'chat smoke-model',
                kind: 1,
                startTimeUnixNano: `${now + 10}000000`,
                endTimeUnixNano: `${now + 20}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
                  { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
                  { key: 'gen_ai.usage.input_tokens', value: { stringValue: '100' } },
                  { key: 'gen_ai.usage.output_tokens', value: { stringValue: '10' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
              {
                // The merge drops duplicate pipeline rows, so a tool can point at a
                // parent that no longer exists. It still belongs below the chat.
                traceId,
                spanId: 'tool-orphan-0001',
                parentSpanId: 'dropped-turn-0001',
                name: 'execute_tool grep',
                kind: 1,
                startTimeUnixNano: `${now + 30}000000`,
                endTimeUnixNano: `${now + 130}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'execute_tool' } },
                  { key: 'gen_ai.tool.name', value: { stringValue: 'grep' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
            ],
          },
        ],
      },
    ],
  });
}

function trailingToolPayload(sessionId, atMs, parentSpanId, spanId) {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              {
                traceId: 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
                spanId: spanId || 'tool-live-0000001',
                ...(parentSpanId ? { parentSpanId } : {}),
                name: 'execute_tool grep',
                kind: 1,
                startTimeUnixNano: `${atMs}000000`,
                endTimeUnixNano: `${atMs + 100}000000`,
                attributes: [
                  { key: 'gen_ai.operation.name', value: { stringValue: 'execute_tool' } },
                  { key: 'gen_ai.tool.name', value: { stringValue: 'grep' } },
                  { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
                ],
              },
            ],
          },
        ],
      },
    ],
  });
}

function chainByOperationParent(payload, byId, operation) {
  const node = payload.nodes.find((item) => item.operation === operation);
  const parent = node ? byId.get(node.parentId) : undefined;
  return parent ? parent.operation : undefined;
}

function turnsPayload(sessionId, idSuffix = '') {
  const now = Date.now();
  const root = 'cccccccccccccccccccccccccccccccc';
  const id = (value) => `${value}${idSuffix}`;
  const chat = (traceId, spanId, start) => ({
    traceId,
    spanId: id(spanId),
    name: 'chat smoke-turn',
    kind: 1,
    startTimeUnixNano: `${start}000000`,
    endTimeUnixNano: `${start + 100}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'chat' } },
      { key: 'gen_ai.response.model', value: { stringValue: 'smoke-model' } },
      { key: 'gen_ai.usage.input_tokens', value: { stringValue: '100' } },
      { key: 'gen_ai.usage.output_tokens', value: { stringValue: '10' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  });
  const turn = (spanId, start) => ({
    traceId: root,
    spanId: id(spanId),
    // Parent is the host session span, which is not ingested: that is what marks
    // a root invoke_agent (a subagent's invoke_agent hangs below execute_tool).
    parentSpanId: 'host-session-00000',
    name: 'invoke_agent',
    kind: 1,
    startTimeUnixNano: `${start}000000`,
    endTimeUnixNano: `${start + 500}000000`,
    attributes: [
      { key: 'gen_ai.operation.name', value: { stringValue: 'invoke_agent' } },
      { key: 'gen_ai.conversation.id', value: { stringValue: sessionId } },
    ],
  });

  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'github-copilot' } },
          ],
        },
        scopeSpans: [
          {
            spans: [
              turn('turn-a-000000001', now),
              chat('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1', 'chat-a1-000000001', now + 10),
              chat('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2', 'chat-a2-000000001', now + 20),
              turn('turn-b-000000001', now + 1_000),
              chat('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1', 'chat-b1-000000001', now + 1_010),
            ],
          },
        ],
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

  // The topology follows the session range: a window with no rows must not fall
  // back to the whole session.
  const futureFrom = Date.now() + 86_400_000;
  const outside = JSON.parse(
    (
      await request(
        `${base}/api/session/${sessionId}/trace?budget=10&from=${futureFrom}&to=${futureFrom + 60_000}`,
      )
    ).body,
  );
  check('trace honours from/to', outside.total, 0);
  check('trace window returns no nodes', outside.nodes.length, 0);
  check(
    'trace rejects a half range',
    (
      await request(
        `${base}/api/session/${sessionId}/trace?from=${futureFrom}`,
      )
    ).status,
    400,
  );

  // The session snapshot no longer carries per-turn rows: the agent run list is the
  // per-turn view, and a turn without a root invoke_agent must not invent one.
  const sessionPayload = JSON.parse(
    (await request(`${base}/api/session/${sessionId}`)).body,
  );
  check('GET /api/session has no turns payload', sessionPayload.turns, undefined);
  check(
    'session payload keeps the aggregate calls',
    sessionPayload.summary.calls,
    6,
  );

  // An approval span carries a tool name but is its own operation; it must show up
  // in the topology with kind and result folded into the label.
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: turnPayload(sessionId),
  });
  const withApproval = JSON.parse(
    (await request(`${base}/api/session/${sessionId}/trace?budget=100`)).body,
  );
  const approval = withApproval.nodes.find(
    (node) => node.operation === 'permission',
  );
  check('trace exposes approval nodes', Boolean(approval), true);
  check('approval node kind', approval?.kind, 'permission');
  check(
    'approval label carries kind, result and tool',
    approval?.label,
    'shell · approved · powershell',
  );
  const approvalSession = 'smoke-approval';
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: approvalWithoutToolNamePayload(approvalSession),
  });
  const approvalTrace = JSON.parse(
    (await request(`${base}/api/session/${approvalSession}/trace?budget=50`)).body,
  );
  const borrowed = approvalTrace.nodes.find(
    (node) => node.operation === 'permission',
  );
  check(
    'approval borrows the tool from its parent',
    borrowed?.label,
    'read · approved · grep',
  );
  const afterApproval = JSON.parse(
    (await request(`${base}/api/session/${sessionId}`)).body,
  );
  check(
    'events still count the approval',
    afterApproval.events.some((row) => row.operation === 'permission'),
    true,
  );

  // One root invoke_agent per turn: both runs are main agents, and a tool that
  /// arrives after them must not create another one.
  const turnSession = 'smoke-turns';
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: turnsPayload(turnSession),
  });
  const withRuns = JSON.parse(
    (await request(`${base}/api/session/${turnSession}`)).body,
  );
  check('runs are detected from root invoke_agent', withRuns.agentRuns.length, 2);
  check(
    'every run is a main agent',
    withRuns.agentRuns.filter((row) => row.isRoot).length,
    2,
  );
  const now = Date.now();
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: trailingToolPayload(turnSession, now + 90_000),
  });
  const afterTail = JSON.parse(
    (await request(`${base}/api/session/${turnSession}`)).body,
  );
  check('a trailing tool does not add a run', afterTail.agentRuns.length, 2);
  // The tool hangs below the model call that requested it, and the approval below
  // the tool: chat → execute_tool → permission.
  const chain = JSON.parse(
    (await request(`${base}/api/session/${approvalSession}/trace?budget=50`)).body,
  );
  const chainById = new Map(chain.nodes.map((node) => [node.id, node]));
  const chainTool = chain.nodes.find((node) => node.operation === 'execute_tool');
  const chainChat = chainTool ? chainById.get(chainTool.parentId) : undefined;
  check('a tool hangs below a chat span', chainChat?.operation, 'chat');
  check(
    'an approval hangs below its tool',
    chainByOperationParent(chain, chainById, 'permission'),
    'execute_tool',
  );

  // A tool whose parent row was dropped by the pipeline merge used to sit at depth
  // 0 next to the model calls; it must still hang below the chat.
  const orphanSession = 'smoke-orphan';
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: orphanToolPayload(orphanSession),
  });
  const orphanTrace = JSON.parse(
    (await request(`${base}/api/session/${orphanSession}/trace?budget=50`)).body,
  );
  const orphanById = new Map(orphanTrace.nodes.map((node) => [node.id, node]));
  const orphanTool = orphanTrace.nodes.find(
    (node) => node.operation === 'execute_tool',
  );
  const orphanChat = orphanTool ? orphanById.get(orphanTool.parentId) : undefined;
  check(
    'a tool with a dropped parent still hangs below a chat',
    orphanChat?.operation,
    'chat',
  );
  check(
    'the orphan tool is indented under the chat',
    orphanTool ? orphanTool.depth - orphanChat.depth : -1,
    1,
  );

  // Hook statistics come from span events, not from execute_hook spans.
  const hookSession = 'smoke-hooks';
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: hookPayload(hookSession),
  });
  const hookPayloadJson = JSON.parse(
    (await request(`${base}/api/session/${hookSession}`)).body,
  );
  check('hooks are groups of runs', hookPayloadJson.hooks.length, 2);
  check(
    'only hook.end is counted',
    hookPayloadJson.hooks.reduce((total, row) => total + row.count, 0),
    2,
  );
  check(
    'hook duration comes from hook.end seconds',
    Math.round(
      hookPayloadJson.hooks.find((row) => row.hookType === 'preToolUse')
        ?.avgDurationMs ?? -1,
    ),
    250,
  );
  check(
    'agent runs are detected from invoke_agent',
    hookPayloadJson.agentRuns.length,
    1,
  );
  check(
    'a root turn with a missing parent is root',
    hookPayloadJson.agentRuns[0]?.isRoot,
    true,
  );
  check(
    'the run name comes from the span name',
    hookPayloadJson.agentRuns[0]?.agentName,
    'explore',
  );

  // A turn row carries its main agent and counts the subagent runs started inside
  // it, so the two tables no longer need to be read side by side.
  const agentSession = 'smoke-agent-turn';
  await request(`${base}/otlp/agent-host`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: agentTurnPayload(agentSession),
  });
  const agentTurn = JSON.parse(
    (await request(`${base}/api/session/${agentSession}`)).body,
  );
  check(
    'the main agent is named from the span name',
    agentTurn.agentRuns.find((row) => row.isRoot)?.agentName,
    'main-agent',
  );
  check(
    'the subagent run is kept apart from the main agent',
    agentTurn.agentRuns.filter((row) => !row.isRoot).length,
    1,
  );
  // A root run reports its whole window (the same numbers as the old turn row),
  // while a nested run reports only its own children.
  check(
    'the run reports the usage of its own calls',
    agentTurn.agentRuns.find((row) => row.isRoot)?.inputTokens,
    300,
  );
  check(
    'the run reports cached tokens',
    agentTurn.agentRuns.find((row) => row.isRoot)?.cacheReadTokens,
    200,
  );
  check(
    'a subagent run reports only its own calls',
    agentTurn.agentRuns.find((row) => !row.isRoot)?.inputTokens,
    200,
  );
  check(
    'a subagent run reports its own cached tokens',
    agentTurn.agentRuns.find((row) => !row.isRoot)?.cacheReadTokens,
    120,
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
