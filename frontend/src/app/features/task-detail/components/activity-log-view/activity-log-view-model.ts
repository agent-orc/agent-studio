import type { SafeHtml } from '@angular/platform-browser';
import type {
  ActivityLogGroup,
  ActivityLogKind,
  ConversationTurn,
  ParsedSteer,
  ToolBurstBin,
} from '../activity-log.parser';
import type { ParsedRuntimeSentinels } from '../runtime-sentinel.parser';

export interface ToolChip {
  kind: ActivityLogKind;
  /** Display name in the chip ("Read", "Grep", "Edit"). */
  label: string;
  count: number;
}

export interface RenderedTurn {
  turn: ConversationTurn;
  /** Parsed readable markers; Trace still owns the untouched raw lines. */
  sentinels: ParsedRuntimeSentinels;
  bodyHtml: SafeHtml | null;
  /**
   * For tool bursts: per-kind chips so the reader sees "Read ×12  Grep ×5"
   * at a glance instead of a single combined sentence. Built once per turn
   * so the template doesn't re-stringify on every change-detection pass.
   */
  toolChips: ToolChip[];
  /** Compact "4s" / "1m 20s" string, or empty when the burst was effectively instant. */
  toolDuration: string;
  /** Per-kind bins for the expanded detail (lazily consumed by the template). */
  toolBins: ToolBurstBin[];
  /**
   * Set when this orchestrator turn is a [steer] message. Drives a
   * dedicated card in the conversation view: question-mark icon, the
   * one-line Need / Why ask, optional option buttons that pre-fill the
   * compose box, and a "Send screenshot" affordance when the Need
   * mentions a screenshot.
   */
  steer?: ParsedSteer;
}

/**
 * Treats blank/separator lines and lone session-init frames as "debug noise"
 * so Trace mode is readable without checkboxes. Anything substantive (real
 * tool calls, agent text, errors, user messages) survives.
 */
export function isDebugNoise(group: ActivityLogGroup): boolean {
  if (!group.lines.length) return true;
  const allBlank = group.lines.every((line) => !line.text || line.text.trim() === '');
  if (allBlank) return true;
  if (group.kind === 'message' && /^●\s*Session\b/i.test(group.title)) return true;
  if (group.kind === 'message' && /^●\s*frame\b/i.test(group.title)) return true;
  if (group.kind === 'other' && /^Codex\b/i.test(group.title)) return true;
  return false;
}

/**
 * One chip per kind seen in the burst, in a deterministic order so the layout
 * doesn't shuffle as new groups stream in. Counts come straight from the
 * pre-aggregated summary (which already accounts for parser-level batches).
 */
export function buildToolChips(turn: ConversationTurn): ToolChip[] {
  const summary = turn.toolSummary;
  if (!summary) return [];
  const order: ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'other'];
  const chips: ToolChip[] = [];
  for (const kind of order) {
    const count = summary.counts[kind];
    if (!count) continue;
    chips.push({ kind, label: chipKindLabel(kind), count });
  }
  return chips;
}

export function roleHeading(kind: ConversationTurn['kind']): string {
  switch (kind) {
    case 'agent': return 'Agent';
    case 'user': return 'You';
    case 'system': return 'System';
    case 'tools': return 'Tools';
    case 'orchestrator': return 'Orchestrator';
    case 'supervisor': return 'Supervisor';
  }
}

export function escapeForPlain(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}

/** Short, capitalised kind label for the chip face ("Read", "Grep", "Edit"). */
function chipKindLabel(kind: ActivityLogKind): string {
  switch (kind) {
    case 'read': return 'Read';
    case 'search': return 'Grep';
    case 'command': return 'Run';
    case 'edit': return 'Edit';
    case 'task': return 'Task';
    case 'todo': return 'Todo';
    case 'error': return 'Error';
    case 'message': return 'Msg';
    case 'orchestrator': return 'Orch';
    case 'supervisor': return 'Sup';
    case 'other': return 'Other';
  }
}
