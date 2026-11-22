import http from 'node:http';
import { context } from '@opentelemetry/api';
import { getRPCMetadata, RPCType } from '@opentelemetry/core';
import { checkHealth } from './health.mjs';
import { log } from './logger.mjs';

const ROUTES = new Set(['/', '/health']);

/** The low-cardinality route of a request path; it becomes the http.route metric attribute. */
export function routeOf(url) {
  const path = new URL(url ?? '/', 'http://localhost').pathname;
  return ROUTES.has(path) ? path : null;
}

export function createHandler({ health = checkHealth } = {}) {
  return async (request, response) => {
    const route = routeOf(request.url);
    // Plain node:http has no router; name the route so Operations can rank endpoints.
    const metadata = getRPCMetadata(context.active());
    if (metadata?.type === RPCType.HTTP) metadata.route = route ?? 'unmatched';
    try {
      if (route === '/health') {
        const report = await health();
        if (report.status !== 'ok') log('warn', 'Health check failed', { 'health.status': report.status });
        response.writeHead(report.status === 'ok' ? 200 : 503, { 'content-type': 'application/json' });
        response.end(JSON.stringify(report));
        return;
      }
      if (route === '/') {
        response.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
        response.end('Hello world\n');
        return;
      }
      response.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      response.end('Not found\n');
    } catch (error) {
      log('error', 'Request failed', { 'error.type': error?.name ?? 'Error' });
      response.writeHead(500, { 'content-type': 'text/plain; charset=utf-8' });
      response.end('Internal error\n');
    }
  };
}

if (process.argv[1] === new URL(import.meta.url).pathname) {
  const port = Number(process.env.PORT || 3000);
  http.createServer(createHandler()).listen(port, '0.0.0.0', () => log('info', 'Service started', { 'server.port': port }));
}
