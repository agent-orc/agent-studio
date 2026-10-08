// Template: move one task between lanes.
//
// Usage:
//   TASKBOARD_PROJECT="Agent Task Processor" node move-state.js <taskId> <targetState>
//
// Example:
//   node move-state.js fix-card-delete-bug 6-completed

import http from 'node:http';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031);
const TARGET_PROJECT = process.env.TASKBOARD_PROJECT ?? 'Agent Task Processor';

const taskId = process.argv[2];
const targetState = process.argv[3];

if (!taskId || !targetState) {
  console.error('usage: node move-state.js <taskId> <targetState>');
  console.error('       targetState: 0-backlog | 1-preparation | 2-ready |');
  console.error('                    2-ready | 6-completed | 7-archive | ...');
  process.exit(1);
}

function request(method, path, bodyObj = null) {
  return new Promise(resolve => {
    const body = bodyObj ? JSON.stringify(bodyObj) : '';
    const req = http.request({
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

async function resolveWatchPath() {
  const res = await request('GET', '/api/watch-paths');
  if (res.status !== 200) throw new Error(`watch-path lookup failed: ${res.status} ${res.body}`);
  const entries = JSON.parse(res.body);
  const entry = entries.find(p => p.name === TARGET_PROJECT)
    ?? entries.find(p => p.name?.toLowerCase().includes(TARGET_PROJECT.toLowerCase()));
  if (!entry?.path) throw new Error(`No watchPath found for project "${TARGET_PROJECT}"`);
  return entry.path;
}

const watchPath = await resolveWatchPath();
const path = `/api/tasks/${encodeURIComponent(taskId)}/move?watchPath=${encodeURIComponent(watchPath)}`;
// A scripted lane move is an operator revoke. Steering requires a queued
// follow-up and should use the dedicated continuation route.
const res = await request('POST', path, { targetState, runIntent: 'revoke' });
console.log('status:', res.status, '| body:', res.body.slice(0, 200) || '(empty)');
