import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { taskApiRequest } from './access.js';

test('task API scripts add a bearer only when configured', async () => {
  const seen = [];
  const server = http.createServer((req, res) => {
    seen.push({ path: req.url, authorization: req.headers.authorization, clientId: req.headers['x-client-id'] });
    res.writeHead(204); res.end();
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  const priorUrl = process.env.TASKBOARD_URL;
  const priorToken = process.env.AGENT_STUDIO_API_TOKEN;
  try {
    process.env.TASKBOARD_URL = `http://127.0.0.1:${address.port}`;
    const send = () => new Promise((resolve, reject) => {
      const req = taskApiRequest({ hostname: '127.0.0.1', port: 1, path: '/api/tasks',
        method: 'GET', headers: { 'X-Client-Id': 'local-default' } }, res => {
        res.resume(); res.on('end', resolve);
      });
      req.on('error', reject); req.end();
    });
    delete process.env.AGENT_STUDIO_API_TOKEN;
    await send();
    process.env.AGENT_STUDIO_API_TOKEN = 'synthetic-token-for-test';
    await send();
    assert.deepEqual(seen, [
      { path: '/api/tasks', authorization: undefined, clientId: 'local-default' },
      { path: '/api/tasks', authorization: 'Bearer synthetic-token-for-test', clientId: 'local-default' },
    ]);
  } finally {
    if (priorUrl === undefined) delete process.env.TASKBOARD_URL;
    else process.env.TASKBOARD_URL = priorUrl;
    if (priorToken === undefined) delete process.env.AGENT_STUDIO_API_TOKEN;
    else process.env.AGENT_STUDIO_API_TOKEN = priorToken;
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
});
