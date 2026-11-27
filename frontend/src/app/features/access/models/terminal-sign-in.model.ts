/**
 * Terminal sign-in (ES-3, Dossier operator-elevated-session): `studio-op
 * login` starts a sign-in, the Owner confirms it on the page at
 * `#/terminal-sign-in` with a fresh second factor, and the terminal's next
 * poll receives the operator session.
 */

export const TERMINAL_SIGN_IN_ROUTE = 'terminal-sign-in';
/** The eight-character alphabet of a user code (RFC 8628 base-20 consonants). */
export const TERMINAL_SIGN_IN_ALPHABET = 'BCDFGHJKLMNPQRSTVWXZ';

export interface TerminalSignInClass {
  id: string;
  title: string;
  /** `batch` classes need one Owner tap per batch, `direct-action` classes act on their own (ES-7). */
  mode: 'batch' | 'direct-action';
}

export interface TerminalSignInAgent {
  id: string;
  name: string;
  lifetimeHours?: readonly number[];
  defaultLifetimeHours?: number;
}

export interface TerminalSignInLookup {
  signInId: string;
  userCode: string;
  state: string;
  deviceLabel: string;
  keyThumbprint: string;
  bindingKind?: 'tpm' | 'secure-enclave' | 'memory';
  deviceKeyThumbprint?: string | null;
  /** Operator agents that already know this device key. */
  knownForAgentIds: readonly string[];
  requestedAgent: string | null;
  requestedAgentId: string | null;
  requestedClasses: readonly TerminalSignInClass[];
  requestedAddress: string | null;
  browserAddress: string | null;
  addressMatches: boolean;
  createdAt: string;
  expiresAt: string;
  lifetimeHours: readonly number[];
  defaultLifetimeHours: number;
  agents: readonly TerminalSignInAgent[];
}

export interface TerminalSignInConfirmation {
  signInId: string;
  operatorAgentId: string;
  sessionId: string;
  actor: string;
  chosenEnd?: string | null;
  chosenLifetimeHours?: number | null;
  expiresAt: string;
  classes: readonly string[];
}

/** The route and the code a confirmation link carries, from a location hash; null for any other hash. */
export function terminalSignInFromHash(hash: string): { code: string | null } | null {
  const raw = hash.replace(/^#\/?/, '');
  const queryIndex = raw.indexOf('?');
  const path = (queryIndex >= 0 ? raw.slice(0, queryIndex) : raw).replace(/\/+$/, '');
  if (path !== TERMINAL_SIGN_IN_ROUTE) return null;
  const code = new URLSearchParams(queryIndex >= 0 ? raw.slice(queryIndex + 1) : '').get('code');
  return { code: normalizeSignInCode(code) };
}

/** Upper case, without separators; null unless it has eight characters of the code alphabet. */
export function normalizeSignInCode(value: string | null | undefined): string | null {
  if (!value) return null;
  const code = value.replace(/[\s-]/g, '').toUpperCase();
  return code.length === 8 && [...code].every(character => TERMINAL_SIGN_IN_ALPHABET.includes(character)) ? code : null;
}

/** `WDJBMJHT` as `WDJB-MJHT`, the way the terminal prints it. */
export function displaySignInCode(code: string): string {
  return code.length === 8 ? `${code.slice(0, 4)}-${code.slice(4)}` : code;
}

/** The first 16 characters of a key thumbprint; the full value goes into a title attribute. */
export function shortThumbprint(thumbprint: string | null | undefined): string {
  return thumbprint ? (thumbprint.length > 16 ? `${thumbprint.slice(0, 16)}…` : thumbprint) : '';
}

export function deviceBindingLabel(kind: string | null | undefined): string {
  return kind === 'tpm' ? 'TPM key (not yet verified on real hardware)' : kind === 'secure-enclave' ? 'Secure Enclave key (not yet verified on real hardware)' : 'Memory key';
}
