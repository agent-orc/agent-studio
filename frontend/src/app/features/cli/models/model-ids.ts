// Frontend-only ids for fixture data and initial component state before the
// server catalog/defaults load. Selectable model lists still come from /api/cli.
export const MODEL_IDS = {
  claudeOpus5: 'claude-opus-5',
  claudeFable51: 'claude-fable-5-1',
  claudeSonnet5: 'claude-sonnet-5',
  claudeOpus47: 'claude-opus-4-7',
  claudeHaiku45: 'claude-haiku-4-5',
  claudeSonnet46: 'claude-sonnet-4-6',
  gpt5Codex: 'gpt-5-codex',
  // Flagship Codex model once the installed codex CLI advertises it (AGT-2025).
  // Availability follows the live catalog from /api/cli; this id only seeds
  // fixtures and the effective-model display before the catalog hydrates.
  gpt56Sol: 'gpt-5.6-sol',
} as const;

export const CLAUDE_FALLBACK_MODEL_ID = MODEL_IDS.claudeHaiku45;
