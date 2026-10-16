// Template: create one task through the application API.
//
// Usage:
//   1. Adjust PORT / TARGET_PROJECT below, or pass env vars:
//        TASKBOARD_PORT=5031 TASKBOARD_PROJECT="Agent Task Processor" node create-task.js
//   2. Fill `id`, `title`, `targetState`, `taskType`, and `promptMarkdown`.
//   3. Keep `agent` and `cliType` on the same real CLI value.
//
// Returns 200 with `{ id }` on success. A 409 usually means the slug already
// exists or the watchPath was not resolved from GET /api/watch-paths.

import { taskApiRequest } from './access.js';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031); // stable; use 5030 for dev
const TARGET_PROJECT = process.env.TASKBOARD_PROJECT ?? 'Agent Task Processor';
const CLI_TYPES = new Set(['claude', 'codex', 'copilot', 'gemini']);

const task = {
  id: 'REPLACE-ME-stable-slug',
  title: 'REPLACE ME - Human-readable title shown on the card',
  targetState: '2-ready',     // initial lane; see references/states.md
  order: 0,                    // position within lane; 0 = top of new batch
  taskType: 'chore',           // bug | feature | chore
  agent: 'codex',              // claude | codex | copilot | gemini
  cliType: 'codex',            // keep in lockstep with agent
  watchPath: '',               // resolved live below; do not hard-code rootPath
  promptMarkdown: [
    '## Context',
    '',
    'Replace this body with the full task prompt as Markdown.',
    '',
    '## Acceptance Criteria',
    '',
    '1. ...',
  ].join('\n'),
};

function request(method, path, bodyObj = null) {
  return new Promise(resolve => {
    const body = bodyObj ? JSON.stringify(bodyObj) : '';
    const req = taskApiRequest({
      hostname: HOST,
      port: PORT,
      path,
      method,
      headers: {
        'Content-Type': 'application/json',
        'X-Client-Id': 'local-default',
        'Content-Length': Buffer.byteLength(body),
      },
    }, res => {
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => resolve({ status: res.statusCode, body: data }));
    });
    req.on('error', e => resolve({ status: -1, body: e.message }));
    if (body) req.write(body);
    req.end();
  });
}

function assertCliPair({ agent, cliType }) {
  if (!CLI_TYPES.has(agent) || !CLI_TYPES.has(cliType) || agent !== cliType) {
    throw new Error(
      `agent and cliType must match a real CLI (${[...CLI_TYPES].join(', ')}); got agent=${agent} cliType=${cliType}`,
    );
  }
}

async function resolveWatchPath() {
  const res = await request('GET', '/api/watch-paths');
  if (res.status !== 200) throw new Error(`watch-path lookup failed: ${res.status} ${res.body}`);
  const entries = JSON.parse(res.body);
  const entry = entries.find(p => p.name === TARGET_PROJECT)
    ?? entries.find(p => p.name?.toLowerCase().includes(TARGET_PROJECT.toLowerCase()));
  if (!entry?.path) throw new Error(`No watchPath found for project "${TARGET_PROJECT}"`);
  return entry.path;
}

assertCliPair(task);
task.watchPath = await resolveWatchPath();
const res = await request('POST', '/api/tasks/', task);
console.log('status:', res.status, '| body:', res.body.slice(0, 400) || '(empty)');
