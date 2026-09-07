import { CliType, TaskMode } from '../models/task.model';
import { laneName } from '../models/lane-presentation';

/**
 * Pure formatting helpers used by both the board and detail views.
 *
 * Kept dependency-free so they can be imported from any component or
 * service without DI; they MUST stay side-effect free (no signals, no
 * `Date.now()` calls — pass `now` explicitly when relative times are
 * needed; that's what avoids NG0100 in change-detection passes).
 */

export function formatTokens(n: number): string {
  if (!n) return '0';
  if (n < 1000) return String(n);
  if (n < 1_000_000) return (n / 1000).toFixed(1) + 'k';
  return (n / 1_000_000).toFixed(2) + 'M';
}

export function formatRateWindow(window: string | null): string {
  if (!window) return '?';
  return window.replace(/_/g, '-');
}

/**
 * `now` is passed in (rather than read via Date.now()) so the result is
 * stable across the same change-detection cycle. Callers should source
 * `now` from a tick-signal (`NowTickService`).
 */
export function formatResetIn(epochSeconds: number, now: number): string {
  if (!epochSeconds) return '?';
  const ms = epochSeconds * 1000 - now;
  if (ms <= 0) return 'now';
  const min = Math.floor(ms / 60_000);
  if (min < 2) return `${Math.floor(ms / 1000)}s`;
  if (min < 120) return `in ${min} min`;
  const hrs = ms / 3_600_000;
  if (hrs < 48) return `in ${hrs.toFixed(1)} h`;
  return `in ${Math.floor(hrs / 24)} d`;
}

/**
 * Lane display name. AGT-2715: this used to special-case two lanes and strip
 * the numeric prefix off everything else, which is how surfaces built on it
 * ended up rendering raw slugs like "human-review" next to a proper "Review"
 * elsewhere. It now delegates to the lane presentation catalogue, which keeps
 * the same readable-slug fallback for lane keys the frontend does not know.
 */
export function stateLabel(state: string): string {
  return laneName(state);
}

export function formatTime(dateStr: string): string {
  return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString();
}

export function formatCompactDateTime(dateStr: string): string {
  const date = new Date(dateStr);
  const day = date.toLocaleDateString([], { month: '2-digit', day: '2-digit' });
  const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return `${day} ${time}`;
}

/**
 * Compact human-readable "time since" string for timestamps shown next to
 * job titles. `now` is passed in (not Date.now()) so the result is stable
 * within a change-detection cycle — source from `NowTickService`.
 */
export function formatRelativeShort(dateStr: string, now: number): string {
  const ms = now - new Date(dateStr).getTime();
  if (ms < 0) return 'gerade';
  const sec = Math.floor(ms / 1000);
  if (sec < 60) return 'gerade';
  const min = Math.floor(sec / 60);
  if (min < 60) return `vor ${min} min`;
  const hrs = Math.floor(min / 60);
  if (hrs < 48) return `vor ${hrs} h`;
  const days = Math.floor(hrs / 24);
  if (days < 30) return `vor ${days} d`;
  const months = Math.floor(days / 30);
  if (months < 12) return `vor ${months} mo`;
  return `vor ${Math.floor(months / 12)} y`;
}

export function formatDateTime(dateStr: string): string {
  return new Date(dateStr).toLocaleString([], {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  });
}

export function formatDateTimeUtc(dateStr: string | null | undefined): string {
  if (!dateStr) return '';
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return dateStr;
  return date.toISOString().replace('T', ' ').slice(0, 16) + 'Z';
}

export function formatRelativeTime(dateStr: string | null | undefined, now: number): string {
  if (!dateStr) return '';
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return dateStr;
  const diffMs = now - date.getTime();
  if (diffMs < 0) return 'just now';
  const minutes = Math.round(diffMs / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months}mo ago`;
  return `${Math.round(months / 12)}y ago`;
}

export function formatMultiplier(mult: number | null): string {
  if (mult === null) return '';
  return mult === 0 ? '0×' : `${mult}×`;
}

export function cliTypeLabel(t: CliType): string {
  switch (t) {
    case 'claude':  return 'Claude Code';
    case 'codex':   return 'Codex';
    case 'gemini':  return 'Gemini';
  }
}

// Distinct glyph per CLI so the cost overview, job preview cards, and
// command-deck picker can be told apart at a glance. Choices echo each
// vendor's mark: Anthropic burst (Claude), OpenAI knot (Codex),
// Gemini zodiac (Gemini).
export function cliTypeIcon(t: CliType): string {
  switch (t) {
    case 'claude':  return '✴️';
    case 'codex':   return '🌀';
    case 'gemini':  return '♊';
  }
}

// Single source of truth for the per-mode glyphs shown on kanban cards.
// Kept in sync with the create-dialog mode picker so a card's badge matches
// the icon the user chose at create time (💻 coding / 🗺️ planning / 🔍 research).
export function taskModeIcon(mode: TaskMode): string {
  switch (mode) {
    case 'planning': return '🗺️';
    case 'research': return '🔍';
    case 'concept':  return '◈';
    case 'coding':   return '💻';
  }
}

export function taskModeLabel(mode: TaskMode): string {
  switch (mode) {
    case 'planning': return 'Planning';
    case 'research': return 'Research';
    case 'concept':  return 'Concept';
    case 'coding':   return 'Coding';
  }
}

export function shortModelName(model: string | null | undefined): string {
  if (!model) return 'No model';
  const m = model.trim();
  if (!m) return 'No model';
  const claudeMatch = /^claude-(opus|sonnet|haiku)-(\d+)-(\d+)$/i.exec(m);
  if (claudeMatch) {
    const [, family, major, minor] = claudeMatch;
    return `${family.toLowerCase()} ${major}.${minor}`;
  }
  const slashIdx = m.indexOf('/');
  if (slashIdx >= 0 && slashIdx < m.length - 1) {
    return m.slice(slashIdx + 1);
  }
  return m;
}
