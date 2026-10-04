export type ModelFamily =
  | 'sol'
  | 'luna'
  | 'ter'
  | 'opus'
  | 'sonnet'
  | 'haiku'
  | 'gemini'
  | 'openai'
  | 'human'
  | 'unknown';

export interface ModelLevelPresentation {
  family: ModelFamily;
  modelCode: string;
  levelCode: string | null;
}

const THINKING_LEVEL_CODES: Readonly<Record<string, string>> = {
  minimal: 'min',
  low: 'l',
  medium: 'm',
  high: 'h',
  xhigh: 'xh',
  ultra: 'u',
  max: 'max',
};

/**
 * Shared compact vocabulary for board cards, task detail, and the sibling
 * coding-agent-chat indicator. Keep this parser in sync with the documented
 * cross-repository contract in docs/quality/design/model-level-indicator.md.
 */
export function buildModelLevelPresentation(
  model: string | null | undefined,
  thinkingLevel: string | null | undefined,
  fallbackLabel = '',
): ModelLevelPresentation {
  const normalized = model?.trim().toLowerCase() ?? '';
  const level = thinkingLevel?.trim().toLowerCase() ?? '';
  const levelCode = level ? THINKING_LEVEL_CODES[level] ?? level.slice(0, 3) : null;

  if (!normalized) {
    const human = fallbackLabel.trim().toLowerCase() === 'human';
    return { family: human ? 'human' : 'unknown', modelCode: human ? 'HUM' : '?', levelCode };
  }

  const claude = /^claude-(opus|sonnet|haiku)-(\d+)(?:[.-](\d+))?/i.exec(normalized);
  if (claude) {
    const [, family, major, minor] = claude;
    const prefix = family === 'opus' ? 'OP' : family === 'sonnet' ? 'SON' : 'HAI';
    return {
      family: family as Extract<ModelFamily, 'opus' | 'sonnet' | 'haiku'>,
      modelCode: `${prefix}${major}${minor ? `.${minor}` : ''}`,
      levelCode,
    };
  }

  const gpt = /^gpt-(\d+(?:\.\d+)?)(?:-([a-z][a-z0-9-]*))?/i.exec(normalized);
  if (gpt) {
    const [, version, suffix = ''] = gpt;
    if (suffix === 'sol' || suffix === 'ter' || suffix === 'luna') {
      return { family: suffix, modelCode: suffix === 'luna' ? 'LUN' : suffix.toUpperCase(), levelCode };
    }
    if (suffix === 'codex') {
      return { family: 'openai', modelCode: `COD${version}`, levelCode };
    }
    if (suffix === 'mini') {
      return { family: 'openai', modelCode: `GPT${version}M`, levelCode };
    }
    return { family: 'openai', modelCode: `GPT${version}`, levelCode };
  }

  if (/^o\d/i.test(normalized)) {
    return { family: 'openai', modelCode: normalized.toUpperCase().slice(0, 6), levelCode };
  }

  const gemini = /^gemini-(\d+(?:\.\d+)?)(?:-([a-z]+))?/i.exec(normalized);
  if (gemini) {
    const [, version, variant] = gemini;
    const variantCode = variant === 'flash' ? 'F' : variant === 'pro' ? 'P' : '';
    return { family: 'gemini', modelCode: `GEM${version}${variantCode}`, levelCode };
  }

  const compact = normalized
    .replace(/^(?:models?|anthropic|openai|google)[/:_-]+/, '')
    .replace(/[^a-z0-9.]+/g, '')
    .toUpperCase();
  return { family: 'unknown', modelCode: compact.slice(0, 7) || '?', levelCode };
}
