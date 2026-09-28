#!/usr/bin/env node
// Read-only replay. Store timings and field sizes, never task body text.
import { writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
const base = process.env.STABLE_API_URL ?? 'http://127.0.0.1:5031';
const out = resolve(process.env.MEASURE_OUT ?? 'docs/task-switch-performance/evidence');
const project = process.env.MEASURE_PROJECT ?? 'PROJ-002';
const count = Number(process.env.MEASURE_COUNT ?? 30);
if (!Number.isInteger(count) || count < 1 || count > 100) throw Error('MEASURE_COUNT must be 1..100');
await mkdir(out, { recursive: true });
const headers = { 'X-Client-Id': 'local-default' };
async function get(path, extra = {}) {
  const start = performance.now();
  const response = await fetch(base + path, { headers: { ...headers, ...extra }, signal: AbortSignal.timeout(15000) });
  const headersMs = performance.now() - start;
  const text = await response.text();
  const durationMs = performance.now() - start;
  return { response, text, headersMs, durationMs };
}
const startedAt = new Date().toISOString();
const watch = await get('/api/watch-paths');
if (!watch.response.ok) throw Error(`Watch paths: ${watch.response.status}`);
const telemetry = await get('/api/admin/git-telemetry');
await writeFile(resolve(out, 'git-telemetry-before.json'), telemetry.text + '\n');
const grouped = await get('/api/tasks/grouped?includeLegacyReviewLane=false');
if (!grouped.response.ok) throw Error(`Grouped: ${grouped.response.status}`);
const board = JSON.parse(grouped.text);
const rows = Object.values(board).filter(Array.isArray).flat().filter(t => t.projectName === 'Agent Studio' && !t.fixture && t.kind !== 'epic');
const selected = process.env.MEASURE_KEYS?.split(',') ?? [...new Set([
  rows.find(t => t.state === '0-backlog')?.key,
  rows.find(t => t.state === '3-progress' && t.key !== 'AGT-2910')?.key,
  rows.find(t => t.state === '5-human-review')?.key,
].filter(Boolean))];
if (!selected.length) throw Error('No task cohort');
const samples = [];
async function sample(path, kind, key, extra) {
  try {
    const r = await get(path, extra);
    const s = { kind, key, at: new Date().toISOString(), status: r.response.status,
      headersMs: r.headersMs, durationMs: r.durationMs,
      serverTiming: r.response.headers.get('server-timing'), bytes: Buffer.byteLength(r.text),
      etag: r.response.headers.get('etag'), fields: {} };
    if (r.response.ok && kind === 'detail') {
      const value = JSON.parse(r.text);
      s.state = value.info.state;
      for (const [name, field] of Object.entries(value)) s.fields[name] = Buffer.byteLength(JSON.stringify(field) ?? 'null');
    }
    samples.push(s);
  } catch (e) { samples.push({kind, key, at:new Date().toISOString(), error:e.message}); }
}
for (let i = 0; i < count; i++) {
  const key = selected[i % selected.length];
  await sample(`/api/tasks/${encodeURIComponent(key)}?project=${encodeURIComponent(project)}`, 'detail', key);
  if (i % 3 === 0) await sample('/api/tasks/grouped?includeLegacyReviewLane=false', 'grouped');
  if (i % 3 === 1 && grouped.response.headers.get('etag')) await sample('/api/tasks/grouped?includeLegacyReviewLane=false', 'grouped-conditional', undefined, { 'If-None-Match': grouped.response.headers.get('etag') });
  if (i % 5 === 0) console.log(`Measured ${i + 1}/${count}`);
  await new Promise(r => setTimeout(r, 200));
}
const after = await get('/api/admin/git-telemetry');
await writeFile(resolve(out, 'git-telemetry-after.json'), after.text + '\n');
await writeFile(resolve(out, 'api-replay.json'), JSON.stringify({startedAt, finishedAt:new Date().toISOString(),
  base, browserHost:'Linux runner', transport:'Forwarded connection to operator Windows Stable',
  selected, coldCacheReset:false, interference:'Live operator and background services remain active', samples}, null, 2) + '\n');
console.log(`Saved ${samples.length} samples to ${out}`);
