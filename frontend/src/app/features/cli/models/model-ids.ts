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
  // Lower cost tiers of the gpt-5.6 family (model-routing-policy.md). Unlike
  // gpt56Sol these are backend registry entries (AGT-2707 round 2), so the
  // picker renders one disabled with a reason on a codex-cli that does not
  // offer it instead of hiding it.
  gpt56Terra: 'gpt-5.6-terra',
  gpt56Luna: 'gpt-5.6-luna',
  // Onboarded gpt-6 flagship (AGT-2707). Known to the backend registry, so the
  // picker renders it disabled with a reason on a codex-cli that does not offer
  // it yet instead of hiding it.
  gpt6Astra: 'gpt-6-astra',
} as const;

export const CLAUDE_FALLBACK_MODEL_ID = MODEL_IDS.claudeHaiku45;
