import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..', '..');
const page = resolve(root, 'docs/website/operator-agent/index.html');
const images = resolve(root, 'docs/assets/images/presentation');
const scenes = ['access', 'watch', 'triage', 'timeline', 'operator-log-chat', 'deploy-preflight', 'attention-lease'];
const seed = JSON.parse(readFileSync(resolve(root, 'scripts/presentation-capture/pinned-seed.json'), 'utf8'));

test('operator scenes stay inside the invented pinned vocabulary', () => {
  assert.deepEqual(seed.projects.map(project => project.name), ['Demo App', 'Demo Platform']);
  assert.deepEqual(seed.operatorScenes.map(scene => scene.id), scenes);
  const copy = JSON.stringify(seed.operatorScenes);
  assert.doesNotMatch(copy, /\b(?:AGT|PROJ|SM)-\d+\b/);
  assert.doesNotMatch(copy, /(?:https?:\/\/|secret\s*[:=]|api\.[A-Za-z0-9_-]{12,})/i);
});

test('operator website page has a pinned light and dark capture for every outlined scene', () => {
  assert.ok(existsSync(page), 'operator website page source is missing');
  const html = readFileSync(page, 'utf8');
  assert.match(html, /developers and small teams/i);
  assert.match(html, /technical leads/i);
  for (const [index, scene] of scenes.entries()) {
    for (const theme of ['light', 'dark']) {
      const file = `${String(index + 9).padStart(2, '0')}-operator-${scene}--${theme}--pinned.png`;
      assert.ok(existsSync(resolve(images, file)), `missing ${file}`);
      assert.ok(html.includes(file), `page does not use ${file}`);
    }
  }
});
