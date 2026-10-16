import http from 'node:http';

export function respond(request, response) {
  if (request.url === '/health') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ status: 'ok' }));
    return;
  }
  response.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
  response.end('Hello world\n');
}

if (process.argv[1] === new URL(import.meta.url).pathname) {
  http.createServer(respond).listen(Number(process.env.PORT || 3000), '0.0.0.0');
}
