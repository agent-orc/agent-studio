// Run: node --test tools/studio-op/test/watch-policy.test.mjs
//
// studio-op watch exits with one stable code per event class, the same
// contract as the watcher script it replaces (Dossier "Decision stream").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EXIT, eventExitCode } from '../src/exit-codes.mjs';
import { SseParser } from '../src/sse.mjs';

const DECISION_TYPES = {
  'escalation.opened': EXIT.EVENT_ESCALATION,
  'human-review.ready': EXIT.EVENT_HUMAN_REVIEW,
  'decision-card.opened': EXIT.EVENT_DECISION_CARD,
  'backend.stalled': EXIT.EVENT_STALL,
  'progress.idle-with-ready': EXIT.EVENT_STALL,
  'batch.red': EXIT.EVENT_BATCH,
  'batch.isolating': EXIT.EVENT_BATCH,
  'push.pending-overdue': EXIT.EVENT_PUSH,
  'quota.exhausted': EXIT.EVENT_QUOTA,
  'quota.fallback-active': EXIT.EVENT_QUOTA,
  'stream.gap': EXIT.EVENT_STREAM_GAP,
};

test('every actionable decision type maps to its class exit code', () => {
  for (const [type, code] of Object.entries(DECISION_TYPES)) assert.equal(eventExitCode(type), code, type);
});

test('the heartbeat is not actionable and an unknown type still wakes the watcher', () => {
  assert.equal(eventExitCode('watcher.heartbeat'), null);
  assert.equal(eventExitCode('future.type'), EXIT.EVENT_OTHER);
});

test('exit codes are distinct and stable', () => {
  const values = Object.values(EXIT);
  assert.equal(new Set(values).size, values.length);
  assert.equal(EXIT.OK, 0);
  assert.equal(EXIT.WATCHER_ACTIVE, 9);
  assert.equal(EXIT.ACCEPT_REFUSED, 7);
  assert.equal(EXIT.EVENT_HUMAN_REVIEW, 21);
});

test('the event stream parser joins split chunks and skips comments', () => {
  const frames = [];
  const parser = new SseParser(frame => frames.push(frame));
  parser.push('retry: 5000\n\n: keep-alive\n\nid: 7\nevent: batch.red\nda');
  parser.push('ta: {"type":"batch.red",\r\n');
  parser.push('data: "cursor":"7"}\n\n');
  assert.equal(frames.length, 1);
  assert.deepEqual(frames[0], { id: '7', event: 'batch.red', data: '{"type":"batch.red",\n"cursor":"7"}' });
});

test('a frame without an id keeps the event name and data', () => {
  const frames = [];
  const parser = new SseParser(frame => frames.push(frame));
  parser.push('event: stream.gap\ndata: {"after":"1"}\n\n');
  assert.deepEqual(frames, [{ id: null, event: 'stream.gap', data: '{"after":"1"}' }]);
});
