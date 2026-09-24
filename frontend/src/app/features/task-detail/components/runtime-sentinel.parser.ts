export type AspectSentinelStatus = 'pass' | 'concerns' | 'block';

export interface ParsedAspectSentinel {
  status: AspectSentinelStatus;
  summary: string;
  evidenceChecked: string[];
  missing: string | null;
  classification: string | null;
  detail: string[];
  malformed: string[];
}

export interface ParsedTerminalSentinel {
  kind: 'done' | 'noop' | 'blocked' | 'needs-input';
  detail: string;
}

export interface ParsedRuntimeSentinels {
  text: string;
  aspects: ParsedAspectSentinel[];
  terminals: ParsedTerminalSentinel[];
}

const ASPECT = /\[\[ASPECT_VERDICT:\s*(?<body>.+?)\s*\]\]/gis;
const TERMINAL = /\[\[TASK_(?<kind>DONE|NOOP|BLOCKED|NEEDS_INPUT)(?::(?<detail>[^\]]*))?\]\]/gi;
const KNOWN_FIELDS = new Set(['status', 'summary', 'evidence_checked', 'missing', 'classification']);

/** Parse only known runtime sentinels. Unknown bracketed text remains verbatim. */
export function parseRuntimeSentinels(source: string): ParsedRuntimeSentinels {
  const aspects: ParsedAspectSentinel[] = [];
  const terminals: ParsedTerminalSentinel[] = [];
  let text = source.replace(ASPECT, (_match, ...args: unknown[]) => {
    const groups = args.at(-1) as { body?: string } | undefined;
    const parsed = parseAspectBody(groups?.body ?? '');
    if (!parsed) return _match as string;
    aspects.push(parsed);
    return '';
  });
  text = text.replace(TERMINAL, (_match, ...args: unknown[]) => {
    const groups = args.at(-1) as { kind?: string; detail?: string } | undefined;
    const kind = (groups?.kind ?? '').toLowerCase().replace('_', '-') as ParsedTerminalSentinel['kind'];
    terminals.push({ kind, detail: groups?.detail?.trim() ?? '' });
    return '';
  });
  return {
    text: text.replace(/\n{3,}/g, '\n\n').trim(),
    aspects,
    terminals,
  };
}

function parseAspectBody(raw: string): ParsedAspectSentinel | null {
  const body = raw.replace(/^\s*>\s?/gm, '');
  const fields = new Map<string, string>();
  const detail: string[] = [];
  const malformed = new Set<string>();
  for (const rawPart of body.split(';')) {
    const part = rawPart.trim();
    const equals = part.indexOf('=');
    if (equals <= 0) continue;
    const key = part.slice(0, equals).trim().toLowerCase();
    const value = part.slice(equals + 1).trim();
    if (!KNOWN_FIELDS.has(key)) continue;
    if (fields.has(key)) {
      malformed.add('duplicate-key');
      detail.push(`${key}=${value}`);
    } else {
      fields.set(key, value);
    }
  }
  const status = normalizeStatus(fields.get('status'));
  if (!status) return null;
  return {
    status,
    summary: fields.get('summary') ?? '',
    evidenceChecked: splitList(fields.get('evidence_checked')),
    missing: nullIfEmpty(fields.get('missing')),
    classification: nullIfEmpty(fields.get('classification')),
    detail,
    malformed: [...malformed],
  };
}

function normalizeStatus(value: string | undefined): AspectSentinelStatus | null {
  const token = value?.trim().toLowerCase();
  if (token === 'pass') return 'pass';
  if (token === 'concern' || token === 'concerns') return 'concerns';
  if (token === 'block' || token === 'blocked') return 'block';
  return null;
}

function splitList(value: string | undefined): string[] {
  if (!value?.trim()) return [];
  return value.split(/\s*(?:,|\|)\s*/).map(item => item.trim()).filter(Boolean);
}

function nullIfEmpty(value: string | undefined): string | null {
  return value?.trim() || null;
}

export function aspectSentinelExplanation(aspect: ParsedAspectSentinel): string {
  const lines = [aspect.summary || 'No summary supplied.'];
  if (aspect.evidenceChecked.length > 0) lines.push(`Evidence: ${aspect.evidenceChecked.join(', ')}`);
  if (aspect.missing) lines.push(`Missing: ${aspect.missing}`);
  if (aspect.detail.length > 0) lines.push(`Detail: ${aspect.detail.join('; ')}`);
  if (aspect.malformed.length > 0) lines.push(`Malformed: ${aspect.malformed.join(', ')}`);
  return lines.join('\n');
}

export function terminalSentinelLabel(terminal: ParsedTerminalSentinel): string {
  switch (terminal.kind) {
    case 'done': return 'Task complete';
    case 'noop': return 'No changes needed';
    case 'blocked': return 'Task blocked';
    case 'needs-input': return 'Input needed';
  }
}
