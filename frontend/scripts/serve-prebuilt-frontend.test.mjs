// Dual-loopback bind of the prebuilt frontend server (AGT-3092).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { listenOn, startFrontendServer } from './serve-prebuilt-frontend.mjs';

const INDEX = '<!doctype html><title>prebuilt</title>';
let tmpDir;
let stub;
let stubUrl;
let ipv6Loopback;

const ipv6Probe = () => new Promise(resolve => {
  const probe = net.createServer();
  probe.once('error', error => resolve({ available: false, reason: 'no IPv6 loopback on this host (' + error.code + ')' }));
  probe.listen(0, '::1', () => probe.close(() => resolve({ available: true })));
});

const get = (host, port, urlPath) => new Promise((resolve, reject) => {
  const request = http.get({ host, port, path: urlPath, agent: false, headers: { accept: 'text/html' } }, response => {
    let body = '';
    response.setEncoding('utf8');
    response.on('data', chunk => { body += chunk; });
    response.on('end', () => resolve({ status: response.statusCode, body }));
    response.on('error', reject);
  });
  request.on('error', reject);
});

const connect = (host, port) => new Promise((resolve, reject) => {
  const socket = net.connect({ host, port });
  socket.once('connect', () => { socket.destroy(); resolve(); });
  socket.once('error', reject);
});

const failIpv6With = code => (server, port, host) => {
  if (host !== '::1') return listenOn(server, port, host);
  return Promise.reject(Object.assign(new Error('listen ' + code + ' ' + host + ':' + port), { code }));
};

before(async () => {
  tmpDir = await mkdtemp(path.join(os.tmpdir(), 'serve-prebuilt-'));
  await writeFile(path.join(tmpDir, 'index.html'), INDEX);
  stub = http.createServer((request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/plain' });
    response.end('stub ' + request.url);
  });
  await listenOn(stub, 0, '127.0.0.1');
  stubUrl = 'http://127.0.0.1:' + stub.address().port;
  ipv6Loopback = await ipv6Probe();
});

after(async () => {
  await new Promise(resolve => stub.close(resolve));
  await rm(tmpDir, { recursive: true, force: true });
});

test('both loopbacks serve index.html on one port', async t => {
  const server = await startFrontendServer(tmpDir, 0, stubUrl);
  t.after(() => server.close());
  const ipv4 = await get('127.0.0.1', server.port, '/');
  assert.equal(ipv4.status, 200);
  assert.equal(ipv4.body, INDEX);
  if (!ipv6Loopback.available) { t.skip(ipv6Loopback.reason); return; }
  assert.deepEqual(server.addresses, ['127.0.0.1', '::1']);
  const ipv6 = await get('::1', server.port, '/');
  assert.equal(ipv6.status, 200);
  assert.equal(ipv6.body, INDEX);
});

test('the /api proxy reaches the backend from both loopbacks', async t => {
  const server = await startFrontendServer(tmpDir, 0, stubUrl);
  t.after(() => server.close());
  const ipv4 = await get('127.0.0.1', server.port, '/api/health?from=v4');
  assert.deepEqual(ipv4, { status: 200, body: 'stub /api/health?from=v4' });
  if (!ipv6Loopback.available) { t.skip(ipv6Loopback.reason); return; }
  const ipv6 = await get('::1', server.port, '/api/health?from=v6');
  assert.deepEqual(ipv6, { status: 200, body: 'stub /api/health?from=v6' });
});

test('an unavailable [::1] logs one warning and keeps serving IPv4', async t => {
  const warn = t.mock.method(console, 'warn', () => {});
  const server = await startFrontendServer(tmpDir, 0, stubUrl, { listen: failIpv6With('EADDRNOTAVAIL') });
  t.after(() => server.close());
  assert.deepEqual(server.addresses, ['127.0.0.1']);
  assert.equal(warn.mock.callCount(), 1);
  assert.match(String(warn.mock.calls[0].arguments[0]), /\[::1\]:\d+ unavailable \(EADDRNOTAVAIL\)/);
  const ipv4 = await get('127.0.0.1', server.port, '/');
  assert.deepEqual(ipv4, { status: 200, body: INDEX });
});

test('a taken [::1] port fails the start and frees the IPv4 port', async () => {
  let ipv6Port;
  const listen = (server, port, host) => {
    if (host === '::1') ipv6Port = port;
    return failIpv6With('EADDRINUSE')(server, port, host);
  };
  await assert.rejects(startFrontendServer(tmpDir, 0, stubUrl, { listen }), error => {
    assert.ok(ipv6Port > 0);
    assert.ok(error.message.includes('[::1]:' + ipv6Port), error.message);
    assert.equal(error.cause.code, 'EADDRINUSE');
    return true;
  });
  const fresh = net.createServer();
  await listenOn(fresh, ipv6Port, '127.0.0.1');
  await new Promise(resolve => fresh.close(resolve));
});

test('close() releases every loopback listener', async () => {
  const server = await startFrontendServer(tmpDir, 0, stubUrl);
  await connect('127.0.0.1', server.port);
  await server.close();
  await assert.rejects(connect('127.0.0.1', server.port), { code: 'ECONNREFUSED' });
  if (server.addresses.includes('::1')) await assert.rejects(connect('::1', server.port), { code: 'ECONNREFUSED' });
});
