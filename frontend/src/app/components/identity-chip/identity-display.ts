/**
 * AGT-4034: one place that turns raw identity and host ids into text people
 * can read. Pure functions, no Angular, so every surface (card, timeline,
 * Needs you, Operations) shows the same name and the same alias.
 */

export type IdentityKind = 'human' | 'service-account' | 'persona' | 'unknown';

export interface IdentityDisplay {
  kind: IdentityKind;
  /** Text for the chip: the name, or the short stable fallback form. */
  label: string;
  /** True when the label is the generated fallback because no name is known. */
  fallback: boolean;
  icon: string;
  kindLabel: string;
  /** Full id; shown only in tooltips and detail views, never as chip text. */
  fullId: string;
  tooltip: string;
}

const KIND_ICON: Readonly<Record<IdentityKind, string>> = {
  human: '👤',
  'service-account': '🔑',
  persona: '🎭',
  unknown: '👤',
};
const KIND_LABEL: Readonly<Record<IdentityKind, string>> = {
  human: 'Person',
  'service-account': 'Service account',
  persona: 'Persona',
  unknown: 'Identity',
};
const KIND_SHORT: Readonly<Record<IdentityKind, string>> = {
  human: 'user',
  'service-account': 'sa',
  persona: 'persona',
  unknown: 'id',
};

/** Infers the kind from the id prefix, e.g. `service-account:sa_62c9...`. */
export function identityKindOf(id: string | null | undefined, hint?: string | null): IdentityKind {
  const value = (id ?? '').toLowerCase();
  if (value.startsWith('service-account:') || value.startsWith('sa_') || hint === 'service-account') return 'service-account';
  if (value.startsWith('persona:') || hint === 'persona') return 'persona';
  if (value.startsWith('user:') || value.startsWith('human:') || hint === 'human') return 'human';
  return 'unknown';
}

/** True when a display name is only the raw id (or a long opaque token) in disguise. */
function isRawIdText(text: string, id: string): boolean {
  const t = text.trim();
  return t === id || /^(service-account:)?sa_[0-9a-f]{8,}$/i.test(t) || /^runner_[0-9a-f]{8,}$/i.test(t);
}

export function identityDisplay(
  id: string | null | undefined,
  options: { name?: string | null; tokenName?: string | null; kind?: string | null } = {},
): IdentityDisplay {
  const fullId = (id ?? '').trim();
  const kind = identityKindOf(fullId, options.kind);
  const name = [options.name, options.tokenName]
    .map(value => value?.trim() ?? '')
    .find(value => value && !isRawIdText(value, fullId)) ?? '';
  const tail = fullId.replace(/[^A-Za-z0-9]/g, '').slice(-4).toLowerCase();
  const label = name || `${KIND_SHORT[kind]}·${tail || '????'}`;
  return {
    kind,
    label,
    fallback: !name,
    icon: KIND_ICON[kind],
    kindLabel: KIND_LABEL[kind],
    fullId,
    tooltip: fullId ? `${KIND_LABEL[kind]}: ${label}\n${fullId}` : `${KIND_LABEL[kind]}: ${label}`,
  };
}

// ---- Hosts ----------------------------------------------------------------

/** `runner_<hex>` ids, optionally with a role suffix, are machine ids, not names. */
const RUNNER_HEX = /^runner_[0-9a-f]{8,}(?:[-_.][a-z]+)?$/i;

export function isRawRunnerId(value: string | null | undefined): boolean {
  return !!value && RUNNER_HEX.test(value.trim());
}

const WORDS = [
  'amber', 'birch', 'cedar', 'dune', 'ember', 'fjord', 'glade', 'harbor', 'iris', 'juniper',
  'kelp', 'lark', 'maple', 'nimbus', 'otter', 'pine', 'quartz', 'reed', 'sage', 'tidal',
  'umber', 'vale', 'willow', 'yarrow',
] as const;

function hash(text: string): number {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
  return h >>> 0;
}

/** Stable readable alias for a host without a display name, e.g. `runner-otter-07`. */
export function generatedHostAlias(hostId: string, role?: string | null): string {
  const h = hash(hostId.trim().toLowerCase());
  const word = WORDS[h % WORDS.length];
  const num = String(Math.floor(h / WORDS.length) % 100).padStart(2, '0');
  const prefix = (role ?? '').trim().toLowerCase().replace(/[^a-z]/g, '') || 'runner';
  return `${prefix}-${word}-${num}`;
}

export interface HostDisplay {
  label: string;
  /** True when the label is a generated alias, so the UI can mark it as such. */
  generated: boolean;
  fullId: string;
  tooltip: string;
}

/**
 * Display name for a host. `override` is the owner's rename, `name` the
 * registered display name. A name that is just a raw runner id is ignored.
 */
export function hostDisplay(
  hostId: string | null | undefined,
  options: { name?: string | null; override?: string | null; role?: string | null } = {},
): HostDisplay {
  const fullId = (hostId ?? '').trim();
  const candidates = [options.override, options.name].map(value => value?.trim() ?? '');
  // A readable id such as `agent-runner-01` is already a name; only `runner_<hex>` is not.
  const named = candidates.find(value => value && !isRawRunnerId(value))
    || (fullId && !isRawRunnerId(fullId) ? fullId : '');
  const label = named || (fullId ? generatedHostAlias(fullId, options.role) : 'unknown host');
  const generated = !named;  // true only for a raw runner_<hex> id
  return {
    label,
    generated,
    fullId,
    tooltip: [`Host: ${label}${generated ? ' (generated alias)' : ''}`, fullId].filter(Boolean).join('\n'),
  };
}

/** Replaces every `runner_<hex>` in free text (log lines) with the host label. */
export function replaceRunnerIds(text: string, resolve: (id: string) => string = id => hostDisplay(id).label): string {
  return text.replace(/\brunner_[0-9a-f]{8,}(?:[-_.][a-z]+)?\b/gi, match => resolve(match));
}
