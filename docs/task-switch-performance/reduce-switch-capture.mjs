#!/usr/bin/env node
// Offline reducer. Never reads task response bodies.
import { readFile, writeFile } from 'node:fs/promises';

const [capturePath, tracePath, summaryPath] = process.argv.slice(2);
if (!capturePath) throw Error('Usage: node reduce-switch-capture.mjs capture.json [trace.jsonl] [summary.json]');
const capture = JSON.parse(await readFile(capturePath, 'utf8'));
const traces = new Map();
if (tracePath) {
  for (const line of (await readFile(tracePath, 'utf8')).split(/\r?\n/)) {
    const start = line.indexOf('{"requestId"');
    if (start < 0) continue;
    try {
      const item = JSON.parse(line.slice(start));
      if (item.switchId) {
        const existing = traces.get(item.switchId) ?? [];
        existing.push(item);
        traces.set(item.switchId, existing);
      }
    } catch { /* Incomplete lines are not counted as completed traces. */ }
  }
}
const percentile = (values, p) => {
  if (!values.length) return null;
  const sorted = values.toSorted((a, b) => a - b);
  const rank = p * (sorted.length - 1);
  const low = Math.floor(rank), high = Math.ceil(rank);
  return +(sorted[low] + (sorted[high] - sorted[low]) * (rank - low)).toFixed(3);
};
const cohorts = {};
const sampleClasses = {};
for (const sample of capture.samples ?? []) {
  const list = cohorts[sample.cohort] ??= [];
  list.push(sample);
  const byClass = sampleClasses[sample.sampleClass ?? 'unclassified'] ??= [];
  byClass.push(sample);
}
const reducePopulation = samples => {
  const successful = samples.filter(s => s.outcome === 'ok' && Number.isFinite(s.paintOpportunityMs));
  const joined = samples.flatMap(s => traces.get(s.switchId) ?? []);
  return {
    sampleCount: samples.length, successful: successful.length,
    errors: samples.length - successful.length,
    p50Ms: percentile(successful.map(s => s.paintOpportunityMs), 0.5),
    p95Ms: percentile(successful.map(s => s.paintOpportunityMs), 0.95),
    domReadyP50Ms: percentile(successful.map(s => s.domReadyMs), 0.5),
    domReadyP95Ms: percentile(successful.map(s => s.domReadyMs), 0.95),
    gitSpawns: joined.reduce((sum, t) => sum + (t.gitSpawns ?? 0), 0),
    gitTimeouts: joined.reduce((sum, t) => sum + (t.gitTimeouts ?? 0), 0),
    tracedRequests: joined.length,
    aborted: joined.filter(t => t.outcome === 'aborted').length,
    timeouts: joined.filter(t => t.outcome === 'timeout').length,
  };
};
const summary = {
  sourceRevision: capture.sourceRevision, runEnvironment: capture.runEnvironment,
  sourceVersion: capture.sourceVersion ?? null,
  origin: capture.origin, startedAt: capture.startedAt, finishedAt: capture.finishedAt,
  coldClientReadyMs: capture.coldClientReadyMs ?? null,
  backendReadiness: capture.backendReadiness,
  failure: capture.failure ?? null,
  pageErrors: (capture.errors ?? []).length,
  cohorts: Object.fromEntries(Object.entries(cohorts).map(([name, samples]) => [name, reducePopulation(samples)])),
  sampleClasses: Object.fromEntries(Object.entries(sampleClasses).map(([name, samples]) => [name, reducePopulation(samples)])),
};
const encoded = JSON.stringify(summary, null, 2) + '\n';
if (summaryPath) await writeFile(summaryPath, encoded);
else process.stdout.write(encoded);
