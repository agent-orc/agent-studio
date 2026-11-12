// Serve two production builds with the same gzip policy for the paired probe.
// Usage: node gzip-static-server.mjs <browser directory> <port>
import { createServer } from 'node:http';
import { readFileSync, statSync } from 'node:fs';
import { resolve, sep } from 'node:path';
import { gzipSync } from 'node:zlib';

const [directory, portText] = process.argv.slice(2);
if (!directory || !portText) throw new Error('Usage: node gzip-static-server.mjs <browser directory> <port>');
const root = resolve(directory);
const port = Number(portText);
const cache = new Map();
const mime = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.json': 'application/json', '.svg': 'image/svg+xml' };

createServer((request, response) => {
  const pathname = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
  const target = resolve(root, `.${pathname === '/' ? '/index.html' : pathname}`);
  if (target !== root && !target.startsWith(root + sep)) {
    response.writeHead(403).end();
    return;
  }
  let file = target;
  try {
    if (!statSync(file).isFile()) throw new Error('not a file');
  } catch {
    file = resolve(root, 'index.html');
  }
  let asset = cache.get(file);
  if (!asset) {
    const raw = readFileSync(file);
    const extension = file.slice(file.lastIndexOf('.'));
    asset = { raw, gzip: gzipSync(raw), contentType: mime[extension] ?? 'application/octet-stream' };
    cache.set(file, asset);
  }
  const compressed = /\bgzip\b/.test(request.headers['accept-encoding'] ?? '');
  const body = compressed ? asset.gzip : asset.raw;
  response.writeHead(200, {
    'Content-Type': asset.contentType,
    'Content-Length': body.length,
    'Cache-Control': 'no-store',
    'Vary': 'Accept-Encoding',
    ...(compressed ? { 'Content-Encoding': 'gzip' } : {}),
  });
  response.end(body);
}).listen(port, '127.0.0.1', () => console.log(`Serving ${root} on ${port}`));
