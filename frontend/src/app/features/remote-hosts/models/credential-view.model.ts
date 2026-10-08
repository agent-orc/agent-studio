export interface CredentialReminder {
  id: string;
  thresholdDays: 14 | 7 | 1;
  dueAt: string;
  reason: 'expiry' | 'rotation';
}

/** Task Server presentation projection. It contains no secret locator or value. */
export interface CredentialView {
  installationId: string;
  hostId: string;
  credentialId: string;
  generation: string;
  supersedes: string | null;
  provider: string;
  kind: string;
  sourceLabel: string;
  effectiveSource: 'active' | 'shadowed' | 'absent' | 'unknown';
  scopes: readonly string[];
  owner: string;
  lastVerifiedAt: string | null;
  expiryKnowledge: 'issuer' | 'operator' | 'none' | 'unknown';
  expiresAt: string | null;
  rotationDueAt: string | null;
  outcome: 'healthy' | 'credential_invalid' | 'provider_incident' | 'quota_exhausted' | 'network_failure' | 'indeterminate' | 'not_verified';
  nextProbeAt: string | null;
  evidenceQuality: 'current' | 'clock-skew';
  evidenceRefs: readonly string[];
  runbookId: string | null;
  reminder: CredentialReminder | null;
  renewalAction: 'codex-sign-in' | 'claude-sign-in' | null;
}

export function credentialStatus(view: CredentialView): string {
  if (view.evidenceQuality === 'clock-skew') return 'Clock skew · diagnosis pending';
  switch (view.outcome) {
    case 'healthy': return 'Login verified';
    case 'credential_invalid': return 'Login renewal required';
    case 'provider_incident': return 'Provider incident · retry scheduled';
    case 'quota_exhausted': return 'Quota exhausted · retry scheduled';
    case 'network_failure': return 'Network failure · retry scheduled';
    default: return 'Diagnosis pending';
  }
}

export function credentialDue(view: CredentialView): string {
  const date = (value: string) => new Intl.DateTimeFormat('en', {
    year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC',
  }).format(new Date(value));
  const expiry = view.expiresAt && ['issuer', 'operator'].includes(view.expiryKnowledge)
    ? `Expires ${date(view.expiresAt)} (${view.expiryKnowledge} date)`
    : view.expiryKnowledge === 'none' ? 'No fixed expiry' : 'Expiry unknown';
  return view.rotationDueAt ? `${expiry} · Rotation due ${date(view.rotationDueAt)}` : expiry;
}
