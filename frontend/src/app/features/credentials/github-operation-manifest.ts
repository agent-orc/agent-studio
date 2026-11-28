export interface GitHubOperationTarget {
  transport: 'https' | 'ssh' | 'api' | 'ghcr';
  repository: string;
}

export interface GitHubGrantReference {
  sourceClass: string;
  grantId?: string | null;
  fingerprint?: string | null;
}

export interface GitHubOperationSuccess {
  succeededAt: string;
  receiptId: string;
  credential: GitHubGrantReference;
  commitsPublished?: number | null;
  publishedCommit?: string | null;
}

export interface GitHubCallerReceipt {
  caller: string;
  host: string;
  slot: string;
  checkoutOwner: string;
  role: string;
  binding: string;
  state: string;
  observedAt: string;
  receiptId: string;
  fetch: GitHubOperationTarget | null;
  push: GitHubOperationTarget | null;
  activeCredential: GitHubGrantReference | null;
  lastRead: GitHubOperationSuccess | null;
  lastWrite: GitHubOperationSuccess | null;
}

export interface GitHubOperationManifest {
  callers: readonly { id: string; label: string; source: string; dependency: string; noBuiltInGitHub: boolean }[];
  receipts: readonly GitHubCallerReceipt[];
  writes: readonly {
    caller: string;
    target: GitHubOperationTarget;
    succeededAt: string;
    fetch?: GitHubOperationTarget | null;
    commitsToPublish?: number | null;
  }[];
  serviceStates: Readonly<Record<string, string>> | null;
  appCoverage: readonly {
    broker: string;
    connected: boolean;
    repositories: readonly { repository: string; state: string; installationId: number | null }[];
  }[];
}

export function githubGrantLabel(grant: GitHubGrantReference | null): string {
  if (!grant) return 'Unverified';
  return [grant.sourceClass, grant.grantId, grant.fingerprint].filter(Boolean).join(' · ');
}

export function githubGrantDependencies(receipts: readonly GitHubCallerReceipt[]) {
  const grants = new Map<string, Set<string>>();
  for (const receipt of receipts) {
    const credentials = [
      { grant: receipt.activeCredential, use: receipt.state === 'active' ? 'active' : receipt.state },
      { grant: receipt.lastRead?.credential, use: 'last read' },
      { grant: receipt.lastWrite?.credential, use: 'last write' },
    ];
    for (const { grant, use } of credentials) {
      if (!grant) continue;
      const caller = [receipt.caller, receipt.host, receipt.slot, receipt.binding, use].join(' · ');
      const grantId = grant.grantId;
      const grantKey = grantId && (grantId.includes('-') ? 'Grant ' + grantId.toLowerCase()
        : grant.sourceClass + ' grant ' + grantId);
      const keys = [grantKey, grant.fingerprint].filter((key): key is string => !!key);
      for (const key of keys) {
        const uses = grants.get(key) ?? new Set<string>();
        uses.add(caller);
        grants.set(key, uses);
      }
    }
  }
  return [...grants].map(([grant, callers]) => ({ grant, callers: [...callers].sort() }))
    .sort((a, b) => a.grant.localeCompare(b.grant));
}
