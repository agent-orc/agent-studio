#!/usr/bin/env node
// Summarizes one run.sh output directory into summary.json (AGT-3369).
//
// Usage: node scripts/api-stall-load/summarize.mjs <run-dir>

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
if (!dir) {
  console.error('usage: summarize.mjs <run-dir>');
  process.exit(2);
}

const lines = (file) => (existsSync(join(dir, file)) ? readFileSync(join(dir, file), 'utf8').trim().split('\n').slice(1) : []);
const max = (xs) => xs.reduce((m, x) => Math.max(m, x), 0);
const mean = (xs) => (xs.length ? xs.reduce((s, x) => s + x, 0) / xs.length : 0);
const mb = (bytes) => Math.round(bytes / (1024 * 1024));

// counters.csv: Timestamp,Provider,Counter Name,Counter Type,Value. Heap size is
// the sum of the generation sizes reported for the same timestamp.
const series = new Map();
for (const line of lines('counters.csv')) {
  const parts = line.split(',');
  const value = Number(parts.at(-1));
  const name = parts.slice(2, -2).join(',');
  const at = parts[0];
  if (!series.has(name)) series.set(name, new Map());
  series.get(name).set(at, value);
}
const values = (name) => [...(series.get(name)?.values() ?? [])];
const heapByTime = new Map();
for (const [name, points] of series) {
  if (!name.startsWith('dotnet.gc.last_collection.heap.size')) continue;
  for (const [at, v] of points) heapByTime.set(at, (heapByTime.get(at) ?? 0) + v);
}
const heap = [...heapByTime.values()];
const pause = values('dotnet.gc.pause.time (s / 5 sec)');
const pool = values('dotnet.thread_pool.thread.count ({thread} / 5 sec)');
const queue = values('dotnet.thread_pool.queue.length ({work_item} / 5 sec)');
const gen2 = values('dotnet.gc.collections ({collection} / 5 sec)[gc.heap.generation=gen2]');
const allocated = values('dotnet.gc.heap.total_allocated (By / 5 sec)');
const cpuUser = values('dotnet.process.cpu.time (s / 5 sec)[cpu.mode=user]');
const cpuSystem = values('dotnet.process.cpu.time (s / 5 sec)[cpu.mode=system]');

const threadRows = lines('threads.csv').map((l) => l.split(',').map(Number));
const osThreads = threadRows.map((r) => r[1]).filter(Number.isFinite);
const rssMb = threadRows.map((r) => r[2] / 1024).filter(Number.isFinite);

const probes = lines('probe.csv').map((l) => Number(l.split(',')[1])).filter(Number.isFinite);
const sorted = [...probes].sort((a, b) => a - b);
const pct = (p) => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))] : 0);

const apiLog = existsSync(join(dir, 'api.log')) ? readFileSync(join(dir, 'api.log'), 'utf8') : '';
const stallLines = apiLog.split('\n').filter((l) => l.includes('api-stall delayMs='));
const stallDelays = stallLines.map((l) => Number(/api-stall delayMs=(\d+)/.exec(l)?.[1])).filter(Number.isFinite);

const summary = {
  heapMb: { mean: mb(mean(heap)), max: mb(max(heap)), end: mb(heap.at(-1) ?? 0) },
  rssMb: { mean: Math.round(mean(rssMb)), max: Math.round(max(rssMb)) },
  osThreads: { mean: Math.round(mean(osThreads)), max: max(osThreads), end: osThreads.at(-1) ?? 0 },
  threadPoolThreads: { mean: Math.round(mean(pool)), max: max(pool) },
  threadPoolQueueMax: max(queue),
  gcPauseSeconds: { total: Number(pause.reduce((s, x) => s + x, 0).toFixed(2)), maxPer5s: Number(max(pause).toFixed(2)) },
  gen2Collections: gen2.reduce((s, x) => s + x, 0),
  allocatedMbPerSecond: Math.round(mean(allocated) / 5 / (1024 * 1024)),
  cpuCoresMean: Number(((mean(cpuUser) + mean(cpuSystem)) / 5).toFixed(2)),
  probe: {
    count: probes.length,
    p50Ms: Math.round(pct(50)),
    p99Ms: Math.round(pct(99)),
    maxMs: Math.round(sorted.at(-1) ?? 0),
    over5s: probes.filter((x) => x > 5000).length,
    over1s: probes.filter((x) => x > 1000).length,
  },
  detector: stallDelays.length ? { warnings: stallDelays.length, maxDelayMs: max(stallDelays), over5s: stallDelays.filter((d) => d >= 5000).length } : null,
};
writeFileSync(join(dir, 'summary.json'), JSON.stringify(summary, null, 2) + '\n');
console.log(JSON.stringify(summary, null, 2));
