import assert from 'node:assert/strict';
import test from 'node:test';
import { createHandler, routeOf } from './server.mjs';
import { checkHealth, dependenciesFromEnv } from './health.mjs';
import { log } from './logger.mjs';
import { telemetryEnabled } from './telemetry-config.mjs';

function call(handler, url) {
  return new Promise(resolve => {
    const result = { status: 0, body: '' };
    handler({ url }, {
      writeHead: status => { result.status = status; },
      end: body => { result.body = body; resolve(result); },
    });
  });
}

test('health reports ok with the state of every dependency', async () => {
  const report = await checkHealth({
    dependencies: [{ name: 'orders', url: 'http://orders.test/health' }],
    fetchImpl: async () => ({ ok: true, status: 200 }), env: {},
  });
  assert.equal(report.status, 'ok');
  assert.deepEqual(report.checks.map(item => [item.name, item.status]),
    [['process', 'ok'], ['telemetry', 'disabled'], ['orders', 'ok']]);
});

test('health is down with HTTP 503 when a dependency fails or does not answer', async () => {
  const report = await checkHealth({
    dependencies: [{ name: 'orders', url: 'http://orders.test/health' }, { name: 'cache', url: 'http://cache.test/' }],
    fetchImpl: async url => {
      if (url.includes('orders')) return { ok: false, status: 500 };
      throw Object.assign(new Error('timeout'), { name: 'TimeoutError' });
    },
    timeoutMs: 50, env: {},
  });
  assert.equal(report.status, 'down');
  assert.equal(report.checks.find(item => item.name === 'orders').detail, 'HTTP 500');
  assert.equal(report.checks.find(item => item.name === 'cache').detail, 'No answer within 50 ms');

  const handler = createHandler({ health: async () => report });
  const response = await call(handler, '/health');
  assert.equal(response.status, 503);
  assert.equal(JSON.parse(response.body).status, 'down');
});

test('health endpoint answers 200 while every dependency is up', async () => {
  const response = await call(createHandler({ health: async () => ({ status: 'ok', checks: [] }) }), '/health');
  assert.equal(response.status, 200);
});

test('telemetry is reported once Studio sets the OTLP endpoint', async () => {
  assert.equal(telemetryEnabled({}), false);
  assert.equal(telemetryEnabled({ OTEL_EXPORTER_OTLP_ENDPOINT: 'https://studio.test/api/telemetry/otlp' }), true);
  assert.equal(telemetryEnabled({ OTEL_EXPORTER_OTLP_ENDPOINT: 'https://studio.test/api/telemetry/otlp', OTEL_SDK_DISABLED: 'true' }), false);
  const report = await checkHealth({ dependencies: [], env: { OTEL_EXPORTER_OTLP_ENDPOINT: 'https://studio.test/api/telemetry/otlp' } });
  assert.equal(report.checks.find(item => item.name === 'telemetry').status, 'ok');
});

test('dependencies come from HEALTH_DEPENDENCIES and reject other schemes', () => {
  assert.deepEqual(dependenciesFromEnv({ HEALTH_DEPENDENCIES: 'orders=http://orders:8080/health, cache=https://cache/ping' }),
    [{ name: 'orders', url: 'http://orders:8080/health' }, { name: 'cache', url: 'https://cache/ping' }]);
  assert.deepEqual(dependenciesFromEnv({}), []);
  assert.throws(() => dependenciesFromEnv({ HEALTH_DEPENDENCIES: 'db=postgres://db/app' }), /http or https/);
});

test('routes are low-cardinality and unknown paths are not found', async () => {
  assert.equal(routeOf('/health?probe=1'), '/health');
  assert.equal(routeOf('/users/42'), null);
  assert.equal((await call(createHandler(), '/users/42')).status, 404);
  assert.equal((await call(createHandler(), '/')).body, 'Hello world\n');
});

test('log writes JSON lines at or above LOG_LEVEL', () => {
  const lines = [];
  log('debug', 'hidden', {}, { env: { LOG_LEVEL: 'info' }, write: line => lines.push(line) });
  log('warn', 'shown', { 'health.status': 'down' }, { env: { LOG_LEVEL: 'info' }, write: line => lines.push(line) });
  assert.equal(lines.length, 1);
  assert.deepEqual({ ...JSON.parse(lines[0]), time: undefined },
    { time: undefined, level: 'warn', message: 'shown', 'health.status': 'down' });
});
