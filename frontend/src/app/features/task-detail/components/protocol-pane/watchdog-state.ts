import { CliOutputLine } from '../../../../models/task.model';

/**
 * State the watchdog chip renders. Mirrors backend `WatchdogState` but
 * derived purely from polled output frames, so the chip stays in sync
 * even if the runtime in-memory state on the backend has been evicted.
 */
export type WatchdogPillState = 'healthy' | 'quiet' | 'suspicious' | 'hung' | 'idle';

export interface WatchdogPill {
  state: WatchdogPillState;
  /**
   * Short label shown on the pill itself ("Live", "Quiet 30s", "Killed").
   * No leading glyph: the shared status indicator's tone icon carries it.
   */
  label: string;
  /** Tooltip text with the full reasoning, shown on hover. */
  tooltip: string;
  /** True when the chip should be visible at all. */
  visible: boolean;
}

// Patterns recognise both the legacy `[watchdog]`-prefixed wording and
// the operator-friendly form ("auto-cancelled", "no output for Ns",
// "streaming output again") so old log lines on disk still classify
// correctly.
const KILL_PATTERNS = [
  /Killed after \d+/i,
  /\[watchdog\] Killed/i,
  /auto-cancelled after \d+s? of silence/i,
];
const SUSPICIOUS_PATTERNS = [
  /Still silent at \d+/i,
  /Will kill at \d+/i,
  /Run will be auto-cancelled at \d+/i,
];
const QUIET_PATTERNS = [
  /Agent has been quiet \d+/i,
  /\bQuiet \d+s\b/i,
  /no output for \d+s? yet/i,
];
const RESUMED_PATTERNS = [
  /resumed streaming/i,
  /Back to healthy/i,
  /streaming output again/i,
];

/**
 * Compute the watchdog pill state from the polled cli-output frames and
 * the current wall clock. Logic:
 *
 * - When the run is finished or there is no run, return `idle` (hidden).
 * - Compute silence as `now - lastRealStreamedAt`, where "real" excludes
 *   `[taskboard]`, `[orchestrator]`, `[watchdog]`-stream lines (so the
 *   watchdog's own messages do not extend its grace).
 * - Map silence onto state: <30s healthy, 30-60 quiet, 60-120 suspicious,
 *   >=120 hung. (Mirrors backend defaults; the chip is a passive
 *   reflection, not the source of truth.)
 * - The latest watchdog meta line takes precedence: if the backend says
 *   "Killed", the pill says hung even if a newer line arrived.
 */
export function deriveWatchdogPill(input: {
  lines: readonly CliOutputLine[];
  isRunning: boolean;
  now: Date;
  warmUpGraceSeconds?: number;
  quietSeconds?: number;
  suspiciousSeconds?: number;
  hungSeconds?: number;
}): WatchdogPill {
  const warmUp = input.warmUpGraceSeconds ?? 30;
  const quiet = input.quietSeconds ?? 30;
  const suspicious = input.suspiciousSeconds ?? 60;
  const hung = input.hungSeconds ?? 120;

  if (!input.isRunning) {
    return { state: 'idle', label: '', tooltip: '', visible: false };
  }

  let runStartedAt: Date | null = null;
  let lastRealStreamAt: Date | null = null;
  let latestWatchdogText: string | null = null;

  for (const line of input.lines) {
    const stream = line.stream ?? 'stdout';
    const text = line.text ?? '';
    const ts = line.timestamp ? new Date(line.timestamp) : null;
    if (!ts || Number.isNaN(ts.getTime())) continue;

    if (stream === 'system' && text.startsWith('[taskboard] Started ')) {
      runStartedAt = ts;
      lastRealStreamAt = ts;
      continue;
    }

    // Watchdog meta line is on the orchestrator stream; we identify by
    // any [watchdog*] tag in the text body so both the legacy `[watchdog]`
    // form and the new `[watchdog-warning]` / `[watchdog-timeout]` tags
    // classify the same way.
    if (stream === 'orchestrator' && /\[watchdog[^\]]*\]/i.test(text)) {
      latestWatchdogText = text;
      continue;
    }

    // Skip our own synthetic streams when computing silence.
    if (stream === 'system' || stream === 'orchestrator' || stream === 'watchdog' || stream === 'user') continue;

    lastRealStreamAt = ts;
  }

  if (!runStartedAt) {
    return { state: 'healthy', label: 'Live', tooltip: 'Run starting...', visible: true };
  }

  // Backend's verdict (from the latest [watchdog] meta line) wins when
  // it announces a kill or a resume - it's authoritative.
  if (latestWatchdogText && KILL_PATTERNS.some((re) => re.test(latestWatchdogText!))) {
    return {
      state: 'hung',
      label: 'Killed',
      tooltip: latestWatchdogText,
      visible: true
    };
  }
  if (latestWatchdogText && RESUMED_PATTERNS.some((re) => re.test(latestWatchdogText!))) {
    return {
      state: 'healthy',
      label: 'Live',
      tooltip: latestWatchdogText,
      visible: true
    };
  }
  const now = input.now ?? new Date();
  const lastSeen = lastRealStreamAt ?? runStartedAt;
  const silenceSec = Math.max(0, (now.getTime() - lastSeen.getTime()) / 1000);
  const ageSec = Math.max(0, (now.getTime() - runStartedAt.getTime()) / 1000);
  const watchdogSaysSuspicious =
    latestWatchdogText && SUSPICIOUS_PATTERNS.some((re) => re.test(latestWatchdogText));
  const watchdogSaysQuiet =
    latestWatchdogText && QUIET_PATTERNS.some((re) => re.test(latestWatchdogText));

  if (ageSec < warmUp) {
    return {
      state: 'healthy',
      label: 'Live',
      tooltip: `Run started ${Math.round(ageSec)}s ago, in warm-up grace.`,
      visible: true
    };
  }

  if (silenceSec >= hung) {
    return {
      state: 'hung',
      label: 'Killed',
      tooltip: `Silent for ${Math.round(silenceSec)}s; expected the watchdog to kill at ${hung}s.`,
      visible: true
    };
  }
  if (silenceSec >= suspicious) {
    return {
      state: 'suspicious',
      label: `Watchdog ${Math.round(silenceSec)}s`,
      tooltip: watchdogSaysSuspicious
        ? latestWatchdogText!
        : `No streamed output for ${Math.round(silenceSec)}s. Watchdog will kill the run at ${hung}s if no signal arrives.`,
      visible: true
    };
  }
  if (silenceSec >= quiet) {
    return {
      state: 'quiet',
      label: `Quiet ${Math.round(silenceSec)}s`,
      tooltip: watchdogSaysQuiet
        ? latestWatchdogText!
        : `Agent has been quiet ${Math.round(silenceSec)}s. Watchdog warns at ${suspicious}s.`,
      visible: true
    };
  }

  return {
    state: 'healthy',
    label: 'Live',
    tooltip: `Streaming. Last frame ${Math.round(silenceSec)}s ago.`,
    visible: true
  };
}
