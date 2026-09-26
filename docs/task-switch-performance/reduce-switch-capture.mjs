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
  const finite = values => values.filter(Number.isFinite);
  const markdownBySwitch = successful.map(s => finite((s.markdownConversion ?? [])
    .map(measure => measure.durationMs))).filter(values => values.length)
    .map(values => values.reduce((sum, value) => sum + value, 0));
  const stages = new Map();
  for (const sample of samples) {
    const perSwitch = new Map();
    for (const request of traces.get(sample.switchId) ?? []) {
      for (const [name, stage] of Object.entries(request.stages ?? {})) {
        if (Number.isFinite(stage.ms))
          perSwitch.set(name, (perSwitch.get(name) ?? 0) + stage.ms);
      }
    }
    for (const [name, ms] of perSwitch) {
      const values = stages.get(name) ?? [];
      values.push(ms);
      stages.set(name, values);
    }
  }
  return {
    sampleCount: samples.length, successful: successful.length,
    errors: samples.length - successful.length,
    p50Ms: percentile(successful.map(s => s.paintOpportunityMs), 0.5),
    p95Ms: percentile(successful.map(s => s.paintOpportunityMs), 0.95),
    domReadyP50Ms: percentile(successful.map(s => s.domReadyMs), 0.5),
    domReadyP95Ms: percentile(successful.map(s => s.domReadyMs), 0.95),
    domWorkSampleCount: finite(successful.map(s => s.domWorkMs)).length,
    domWorkP50Ms: percentile(finite(successful.map(s => s.domWorkMs)), 0.5),
    domWorkP95Ms: percentile(finite(successful.map(s => s.domWorkMs)), 0.95),
    markdownSampleCount: markdownBySwitch.length,
    markdownP50Ms: percentile(markdownBySwitch, 0.5),
    markdownP95Ms: percentile(markdownBySwitch, 0.95),
    stageStats: Object.fromEntries([...stages].map(([name, values]) => [name, {
      sampleCount: values.length, p50Ms: percentile(values, 0.5),
      p95Ms: percentile(values, 0.95),
    }])),
    // A Stable build without request traces has missing Git evidence, not zero spawns.
    gitSpawns: joined.length ? joined.reduce((sum, t) => sum + (t.gitSpawns ?? 0), 0) : null,
    gitTimeouts: joined.length ? joined.reduce((sum, t) => sum + (t.gitTimeouts ?? 0), 0) : null,
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
