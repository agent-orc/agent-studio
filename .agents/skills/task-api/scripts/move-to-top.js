// Template: promote a task to the head of its 2-ready queue.
//
// Usage:
//   TASKBOARD_PROJECT="Agent Task Processor" node move-to-top.js <taskId>
//
// Returns { position: 0 } on success. 404 if the task is not found in
// 2-ready or another promotable lane.

import { taskApiRequest } from './access.js';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031);
const TARGET_PROJECT = process.env.TASKBOARD_PROJECT ?? 'Agent Task Processor';

const taskId = process.argv[2];
if (!taskId) {
  console.error('usage: node move-to-top.js <taskId>');
  process.exit(1);
}

function request(method, path) {
  return new Promise(resolve => {
    const req = taskApiRequest({
      hostname: HOST,
      port: PORT,
      path,
      method,
      headers: {
        'X-Client-Id': 'local-default',
        'Content-Length': 0,
      },
    }, res => {
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => resolve({ status: res.statusCode, body: data }));
    });
    req.on('error', e => resolve({ status: -1, body: e.message }));
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
const path = `/api/tasks/${encodeURIComponent(taskId)}/move-to-top?watchPath=${encodeURIComponent(watchPath)}`;
const res = await request('POST', path);
console.log('status:', res.status, '| body:', res.body.slice(0, 200) || '(empty)');
