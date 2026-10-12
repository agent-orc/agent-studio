// Serve a prebuilt frontend bundle and proxy API/WebSocket calls for Playwright.
import http from 'node:http';
import path from 'node:path';
import { createReadStream } from 'node:fs';
import { realpath, stat } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.webmanifest': 'application/manifest+json' };
const isProxy = url => /^\/(api|hubs)(?:[/?]|$)/.test(url);
const proxyOptions = (backend, request) => ({
  hostname: backend.hostname.replace(/^\[|\]$/g, ''), port: backend.port || 80,
  path: request.url, method: request.method,
  headers: { ...request.headers, host: backend.host },
});

export async function createFrontendServer(directory, backendUrl) {
  const root = await realpath(directory);
  const backend = new URL(backendUrl);
  if (backend.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(backend.hostname)) throw new Error('Backend must be a loopback HTTP endpoint');
  if (!(await stat(path.join(root, 'index.html'))).isFile()) throw new Error('Frontend index.html must be a file');
  const server = http.createServer(async (request, response) => {
    if (isProxy(request.url)) {
      const upstream = http.request(proxyOptions(backend, request), result => {
        response.writeHead(result.statusCode, result.headers);
        result.on('error', () => response.destroy());
        result.on('aborted', () => response.destroy());
        result.pipe(response);
      });
      upstream.on('error', () => {
        if (response.headersSent) { response.destroy(); return; }
        response.writeHead(502);
        response.end('Backend unavailable');
      });
      request.on('aborted', () => upstream.destroy());
      request.on('error', () => upstream.destroy());
      response.on('close', () => { if (!response.writableFinished) upstream.destroy(); });
      request.pipe(upstream);
      return;
    }
    if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405, { Allow: 'GET, HEAD' }); response.end(); return; }
    try {
      const raw = decodeURIComponent(request.url.split('?')[0]);
      if (raw.includes('\0') || raw.includes('\\') || raw.split('/').includes('..')) { response.writeHead(400); response.end(); return; }
      let file = path.resolve(root, '.' + raw);
      if (file !== root && !file.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
      let details = await stat(file).catch(error => { if (error.code === 'ENOENT' || error.code === 'ENOTDIR') return null; throw error; });
      if (!details?.isFile()) {
        if (path.extname(raw) || (raw !== '/' && !String(request.headers.accept || '').includes('text/html'))) { response.writeHead(404); response.end(); return; }
        file = path.join(root, 'index.html');
        details = await stat(file);
      }
      const resolved = await realpath(file);
      if (!resolved.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
      response.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream', 'Content-Length': details.size, 'Cache-Control': 'no-cache', 'X-Content-Type-Options': 'nosniff' });
      if (request.method === 'HEAD') response.end();
      else createReadStream(resolved).on('error', () => response.destroy()).pipe(response);
    } catch (error) {
      response.writeHead(error instanceof URIError ? 400 : 500);
      response.end();
    }
  });
  server.on('upgrade', (request, socket, head) => {
    if (!isProxy(request.url)) { socket.destroy(); return; }
    const upstream = http.request(proxyOptions(backend, request));
    // Observe a client FIN while the upstream handshake is pending. Keep any
    // early frames bounded until a remote socket exists to receive them.
    const pending = head.length ? [head] : [];
    let pendingBytes = head.length;
    const collect = chunk => {
      pendingBytes += chunk.length;
      if (pendingBytes > 64 * 1024) { socket.destroy(); upstream.destroy(); return; }
      pending.push(chunk);
    };
    socket.on('data', collect);
    socket.on('end', () => { upstream.destroy(); socket.destroy(); });
    socket.on('close', () => upstream.destroy());
    upstream.on('upgrade', (result, remote, remoteHead) => {
      socket.removeListener('data', collect);
      if (socket.destroyed) { remote.destroy(); return; }
      socket.write('HTTP/1.1 101 Switching Protocols\r\n' + Object.entries(result.headers).map(([name, value]) => name + ': ' + value).join('\r\n') + '\r\n\r\n');
      if (remoteHead.length) socket.write(remoteHead);
      for (const chunk of pending) remote.write(chunk);
      pending.length = 0;
      socket.pipe(remote).pipe(socket);
      socket.on('error', () => remote.destroy());
      remote.on('error', () => socket.destroy());
      socket.on('close', () => remote.destroy());
      remote.on('close', () => socket.destroy());
    });
    upstream.on('response', result => { result.on('error', () => socket.destroy()); socket.end('HTTP/1.1 ' + result.statusCode + ' Upgrade rejected\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'); result.resume(); });
    upstream.on('error', () => socket.destroy());
    socket.on('error', () => upstream.destroy());
    upstream.end();
  });
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [directory, portText, backend] = process.argv.slice(2);
  const port = Number(portText);
  if (!directory || !backend || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Usage: node serve-prebuilt-frontend.mjs <directory> <port> <loopback-backend-url>');
  const server = await createFrontendServer(directory, backend);
  server.listen(port, '127.0.0.1', () => console.log('Prebuilt frontend listening on http://127.0.0.1:' + port));
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close(() => process.exit(0)));
}
