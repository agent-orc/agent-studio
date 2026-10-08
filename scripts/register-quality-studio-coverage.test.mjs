import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import path from 'node:path';

test('coverage registration preserves other Quality Studio sensors', async () => {
  const checkout = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  let update;
  const server = createServer(async (request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.method === 'GET') {
      response.end(JSON.stringify({ repositories: [{
        id: 'subject', displayName: 'Subject', rootPath: checkout,
        enabledReviewKinds: ['code'], inputBudgetCharacters: 1000,
        sensors: [{ id: 'eslint', enabled: true }, { id: 'coverage', enabled: false }],
      }] }));
      return;
    }
    update = JSON.parse(await Array.fromAsync(request).then((chunks) => Buffer.concat(chunks).toString()));
    response.end(JSON.stringify(update));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    const address = server.address();
    const child = spawn(process.execPath, [
      'scripts/register-quality-studio-coverage.mjs',
      `http://127.0.0.1:${address.port}`, 'subject',
    ], { cwd: checkout, stdio: 'pipe' });
    const exit = await new Promise((resolve) => child.on('close', resolve));
    assert.equal(exit, 0);
    assert.equal(update.sensors[0].id, 'eslint');
    assert.deepEqual(update.sensors[1], {
      id: 'coverage', enabled: true,
      configuration: {
        reportPaths: 'coverage/dotnet/**/*.cobertura.xml;coverage/frontend/lcov.info',
      },
    });
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
});
