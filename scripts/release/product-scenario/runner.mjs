// Runs the product scenario steps in order and records one evidence row per
// step. A step whose prerequisite did not pass is skipped with that reason, so
// the report always lists every step of the playthrough. The shape of a row is
// the one the lifecycle scenario (AGT-3178) reports.

export const STEP_STATUS = Object.freeze({
  PASSED: 'passed',
  FAILED: 'failed',
  SKIPPED: 'skipped',
});

/** The quota meter delta between two snapshots, in the report's units. */
export function quotaDelta(before, after) {
  if (!before?.usage || !after?.usage) return null;
  return {
    modelTokens: Math.max(0, (after.usage.modelTokens ?? 0) - (before.usage.modelTokens ?? 0)),
    computeCoreHours: round(Math.max(0, (after.usage.computeCoreHours ?? 0) - (before.usage.computeCoreHours ?? 0)), 4),
    hostingSlots: Math.max(0, (after.usage.hostingSlots ?? 0) - (before.usage.hostingSlots ?? 0)),
  };
}

/**
 * Executes `steps` ({ id, title, phase?, needs?, always?, run(context, record) })
 * one by one. A step with `always` runs even when its needs did not pass;
 * teardown uses it so a failed run still removes the users it invited.
 * `readQuota` returns the workspace quota snapshot or null; it is read around
 * each step so the report shows the quota use per step.
 */
export async function runSteps(steps, { context, clock, readQuota = async () => null, onStep = () => {} }) {
  const results = [];
  const outcome = new Map();
  for (const step of steps) {
    const blocking = step.always ? undefined : (step.needs ?? []).find(id => outcome.get(id) !== STEP_STATUS.PASSED);
    const startedAt = clock.now();
    if (blocking) {
      const row = {
        id: step.id, title: step.title, phase: step.phase ?? 'scenario', actor: step.actor ?? null,
        status: STEP_STATUS.SKIPPED, startedAtUtc: startedAt.toISOString(), endedAtUtc: startedAt.toISOString(),
        durationSeconds: 0, reason: `Needs step "${blocking}", which did not pass.`, evidence: {}, screenshots: [], usage: null,
      };
      outcome.set(step.id, row.status);
      results.push(row);
      onStep(row);
      continue;
    }
    const record = recorder();
    const quotaBefore = await safeQuota(readQuota);
    let status = STEP_STATUS.PASSED;
    let reason;
    try {
      await step.run(context, record);
    } catch (error) {
      status = error instanceof StepSkipped ? STEP_STATUS.SKIPPED : STEP_STATUS.FAILED;
      reason = describe(error);
      if (error instanceof StepFailure || error instanceof StepSkipped) Object.assign(record.evidence, error.evidence);
    }
    const quotaAfter = await safeQuota(readQuota);
    const endedAt = clock.now();
    const row = {
      id: step.id, title: step.title, phase: step.phase ?? 'scenario', actor: step.actor ?? null, status,
      startedAtUtc: startedAt.toISOString(), endedAtUtc: endedAt.toISOString(),
      durationSeconds: round((endedAt - startedAt) / 1000, 2),
      ...(reason ? { reason } : {}),
      evidence: record.evidence, screenshots: record.screenshots, usage: quotaDelta(quotaBefore, quotaAfter),
    };
    outcome.set(step.id, status);
    results.push(row);
    onStep(row);
  }
  return results;
}

/** The overall verdict: failed when any step failed, skipped when one was skipped. */
export function overallStatus(results) {
  if (results.length === 0) return STEP_STATUS.FAILED;
  if (results.some(row => row.status === STEP_STATUS.FAILED)) return STEP_STATUS.FAILED;
  if (results.some(row => row.status === STEP_STATUS.SKIPPED)) return STEP_STATUS.SKIPPED;
  return STEP_STATUS.PASSED;
}

/** A failed expectation inside a step; the message becomes the report reason. */
export class StepFailure extends Error {
  constructor(message, evidence = {}) {
    super(message);
    this.name = 'StepFailure';
    this.evidence = evidence;
  }
}

/** A step that cannot run on this installation; recorded as skipped with the gap as reason. */
export class StepSkipped extends Error {
  constructor(message, evidence = {}) {
    super(message);
    this.name = 'StepSkipped';
    this.evidence = evidence;
  }
}

/** Throws a {@link StepFailure} unless `condition` holds. */
export function expect(condition, message, evidence) {
  if (!condition) throw new StepFailure(message, evidence);
}

/**
 * Polls `probe` until it returns a truthy value or `timeoutMs` passes on the
 * injected clock. Returns the value; throws a StepFailure with `what` on timeout.
 */
export async function waitFor(what, probe, { clock, timeoutMs, intervalMs }) {
  const deadline = clock.now().getTime() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (clock.now().getTime() >= deadline)
      throw new StepFailure(`Timed out after ${Math.round(timeoutMs / 1000)} s waiting for ${what}.`);
    await clock.sleep(intervalMs);
  }
}

export const systemClock = Object.freeze({
  now: () => new Date(),
  sleep: ms => new Promise(resolve => setTimeout(resolve, ms)),
});

function recorder() {
  const evidence = {};
  const screenshots = [];
  return {
    evidence,
    screenshots,
    note(key, value) { evidence[key] = value; },
    screenshot(file, caption) { screenshots.push({ file, caption }); },
  };
}

async function safeQuota(readQuota) {
  try { return await readQuota(); }
  catch { return null; }
}

function describe(error) {
  if (error instanceof StepFailure || error instanceof StepSkipped) return error.message;
  return error?.message ? `${error.name ?? 'Error'}: ${error.message}` : String(error);
}

function round(value, digits) {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}
