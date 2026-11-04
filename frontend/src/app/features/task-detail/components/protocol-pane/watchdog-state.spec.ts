import { describe, expect, it } from 'vitest';
import { deriveWatchdogPill } from './watchdog-state';
import { CliOutputLine } from '../../../../models/task.model';

function line(stream: string, text: string, atSecondsFromBase: number): CliOutputLine {
  // Anchor everything around a single base timestamp so the math is obvious.
  const base = new Date('2026-05-02T12:00:00Z');
  const t = new Date(base.getTime() + atSecondsFromBase * 1000);
  return { stream, text, timestamp: t.toISOString() };
}

const NOW = new Date('2026-05-02T12:02:00Z'); // 120s after base.

describe('deriveWatchdogPill', () => {
  it('hides the chip when the run is not active', () => {
    const pill = deriveWatchdogPill({ lines: [], isRunning: false, now: NOW });
    expect(pill.visible).toBe(false);
    expect(pill.state).toBe('idle');
  });

  it('shows healthy live when streaming recently', () => {
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line('stdout', 'first frame', 5),
        line('stdout', 'still going', 110)
      ],
      isRunning: true,
      now: NOW
    });
    expect(pill.state).toBe('healthy');
    expect(pill.label).toBe('Live');
  });

  it('shows quiet after 30s+ of silence past warm-up', () => {
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line('stdout', 'last frame', 70) // last real frame at +70s; now is +120s -> 50s silence
      ],
      isRunning: true,
      now: NOW
    });
    expect(pill.state).toBe('quiet');
    expect(pill.label).toMatch(/Quiet \d+s/);
  });

  it('shows suspicious after 60s+ of silence', () => {
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line('stdout', 'last frame', 30) // 90s silence at NOW
      ],
      isRunning: true,
      now: NOW
    });
    expect(pill.state).toBe('suspicious');
    expect(pill.label).toMatch(/Watchdog \d+s/);
  });

  it('respects backend kill verdict from the latest watchdog line', () => {
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line('orchestrator', '[watchdog] Killed after 122s of silence.', 122)
      ],
      isRunning: true,
      now: NOW
    });
    expect(pill.state).toBe('hung');
    expect(pill.label).toBe('Killed');
  });

  it('does not let watchdog meta lines reset the silence clock', () => {
    // The orchestrator's own [watchdog] line at +90s must not count as
    // streaming activity. With a real frame at +30s, NOW=120s, silence
    // should still be 90s -> suspicious, not 30s -> healthy.
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line('stdout', 'real frame', 30),
        line('orchestrator', '[watchdog] Agent has been quiet 60s.', 90)
      ],
      isRunning: true,
      now: NOW
    });
    expect(pill.state).toBe('suspicious');
  });

  it('stays healthy inside the warm-up grace even with no frames', () => {
    const earlyNow = new Date('2026-05-02T12:00:20Z'); // 20s in
    const pill = deriveWatchdogPill({
      lines: [line('system', '[taskboard] Started claude CLI', 0)],
      isRunning: true,
      now: earlyNow
    });
    expect(pill.state).toBe('healthy');
  });

  // The operator-friendly watchdog wording (2026-05-15 notifications
  // overhaul) drops the `[watchdog]` literal prefix in favour of a
  // typed tag plus a plain-English body. Both the orchestrator-stream
  // identifier and the kill-pattern matcher must accept the new form
  // so the protocol-pane chip continues to surface the kill verdict.
  it('respects backend kill verdict written in the operator-friendly form', () => {
    const pill = deriveWatchdogPill({
      lines: [
        line('system', '[taskboard] Started claude CLI', 0),
        line(
          'orchestrator',
          '[watchdog-timeout] "Fix git diff container display" (claude): auto-cancelled after 180s of silence. The run will finalize as failed.',
          180
        )
      ],
      isRunning: true,
      now: new Date('2026-05-02T12:03:00Z') // 180s after base
    });
    expect(pill.state).toBe('hung');
    expect(pill.label).toBe('Killed');
  });
});
