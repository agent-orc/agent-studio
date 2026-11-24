// Run: node --test tools/studio-op/test/qr.test.mjs
//
// The QR encoder of studio-op login must produce the same modules as the
// `qrcode` package the frontend uses, for every version and mask it supports.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { MAX_VERSION, capacity, encodeQr, renderQr } from '../src/qr.mjs';

const frontend = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'frontend', 'package.json');
const reference = (() => {
  try { return createRequire(frontend)('qrcode'); } catch { return null; }
})();

test('the encoder matches the qrcode package in byte mode at level M', { skip: !reference && 'frontend/node_modules has no qrcode' }, () => {
  const texts = ['A', 'https://studio.example.test/#/terminal-sign-in?code=WDJBMJHT', 'x'.repeat(100), 'y'.repeat(capacity(MAX_VERSION))];
  let compared = 0;
  for (const text of texts)
    for (let version = 1; version <= MAX_VERSION; version++) {
      if (capacity(version) < Buffer.byteLength(text)) continue;
      for (let mask = 0; mask < 8; mask++) {
        const expected = reference.create([{ data: text, mode: 'byte' }], { errorCorrectionLevel: 'M', version, maskPattern: mask });
        const actual = encodeQr(text, { version, mask });
        assert.equal(actual.size, expected.modules.size);
        for (let y = 0; y < actual.size; y++)
          for (let x = 0; x < actual.size; x++)
            assert.equal(actual.modules[y][x], !!expected.modules.get(y, x), `version ${version} mask ${mask} at ${x},${y}`);
        compared++;
      }
    }
  assert.ok(compared >= 100);
});

test('the terminal rendering has two module rows per line and a quiet zone', () => {
  const { size } = encodeQr('https://studio.example.test/#/terminal-sign-in?code=WDJBMJHT');
  const lines = renderQr('https://studio.example.test/#/terminal-sign-in?code=WDJBMJHT').split('\n');
  assert.equal(lines.length, Math.ceil((size + 4) / 2));
  assert.ok(lines.every(line => [...line].length === size + 4));
  assert.match(lines[0], /^█+$/);
  assert.throws(() => encodeQr('z'.repeat(capacity(MAX_VERSION) + 1)), RangeError);
});
