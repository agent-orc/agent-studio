import type { StatusTone } from '../status-indicator/status-indicator.component';

export type EditorState = 'idle' | 'dirty' | 'saved';

/** Saved wins while the confirmation is showing; otherwise dirty when the source diverges from the parent value. */
export function resolveEditorState(savedAt: number, source: string, committed: string): EditorState {
  if (savedAt > 0) return 'saved';
  return source !== committed ? 'dirty' : 'idle';
}

/** Editor save state to shared status indicator (AGT-3403). Idle is settled, so it stays neutral. */
export const EDITOR_SAVE_STATES: Readonly<Record<EditorState, { tone: StatusTone; label: string }>> = {
  dirty: { tone: 'warning', label: 'Unsaved changes' },
  saved: { tone: 'success', label: 'Saved' },
  idle: { tone: 'neutral', label: 'Saved' },
};

/** Save-state indicator for an editor state; unknown states fall back to idle. */
export function editorSaveState(state: EditorState): { tone: StatusTone; label: string } {
  return EDITOR_SAVE_STATES[state] ?? EDITOR_SAVE_STATES.idle;
}

export const DEFAULT_EDITOR_LOCK_REASON = 'Editing disabled while the CLI is running for this task. Stop it first.';
