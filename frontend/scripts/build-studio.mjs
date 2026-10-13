import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const frontend = path.join(root, 'frontend');
let sha = null;
try {
  const head = execFileSync('git', ['rev-parse', 'HEAD'], {
    cwd: root, encoding: 'utf8', windowsHide: true,
  }).trim();
  if (/^[0-9a-f]{40}$/.test(head)) sha = head;
} catch (error) {
  console.warn('Build commit identity unavailable:', error.message);
}

const cli = path.join(frontend, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const result = spawnSync(process.execPath, [cli, 'build', 'frontend',
  '--define', `STUDIO_BUILD_SHA=${JSON.stringify(sha)}`], {
  cwd: frontend, stdio: 'inherit', windowsHide: true,
});
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
