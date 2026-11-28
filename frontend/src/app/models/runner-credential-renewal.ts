export interface RunnerCredentialRenewal {
  enabled: boolean;
  installationEnabled: boolean;
  runnerEnabled: boolean;
  currentExpiresAt: string | null;
  lastRenewalAt: string | null;
  nextRenewalAt: string | null;
  failingSince: string | null;
  failureReason: string | null;
  lastReplayAt: string | null;
}
