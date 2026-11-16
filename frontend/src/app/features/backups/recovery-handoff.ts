import type { BackupOverview } from './backup-status.model';

export const recoveryRunbookUrl = 'https://github.com/agent-orc/agent-studio/blob/develop/docs/operations/setup/studio-backup-restore.md';

export interface RecoveryInput {
  name: string;
  status: string;
  step: string;
  href: string;
}

/** A handoff contains only public backup metadata. Escrowed material never enters the browser. */
export function recoveryInputs(overview: BackupOverview): RecoveryInput[] {
  const good = overview.lastGood;
  const copied = good && overview.offHost.state === 'current' && overview.offHost.lastUploadedId === good.id;
  const verified = good && overview.lastVerification?.outcome === 'verified'
    && overview.lastVerification.backupId === good.id;
  return [
    {
      name: 'Archive and checksum',
      status: copied ? `Copied ${overview.offHost.lastUploadedAt ?? 'time unknown'}: ${good.id}` : 'Current off-host copy not confirmed',
      step: 'Fetch both files from the off-host target', href: `${recoveryRunbookUrl}#fresh-host-step-2`,
    },
    {
      name: 'Off-host reader account', status: 'Not checked by Studio; the host has write-only access',
      step: 'Fetch both files with a read-capable account', href: `${recoveryRunbookUrl}#fresh-host-step-2`,
    },
    {
      name: 'Restore verification',
      status: verified ? `Verified ${overview.lastVerification!.at}` : 'Current archive not verified',
      step: 'Check verification scope', href: `${recoveryRunbookUrl}#schedule-retention-and-verification`,
    },
    {
      name: 'Backup key copy',
      status: `Not checked by Studio; expected id ${overview.keyId ?? 'unknown'}`,
      step: 'Check escrowed copy fingerprint', href: `${recoveryRunbookUrl}#check-the-escrowed-copies`,
    },
    {
      name: 'Secret store key copy',
      status: `Not checked by Studio; expected id ${overview.secretKeyId ?? 'unknown'}`,
      step: 'Check escrowed copy fingerprint', href: `${recoveryRunbookUrl}#check-the-escrowed-copies`,
    },
    {
      name: 'Host CA private and public keys',
      status: overview.warnings.some(w => w.code === 'host-ca-mismatch') ? 'Host CA mismatch; repair before host loss' : 'Not checked by Studio',
      step: 'Bring matching escrowed CA files', href: `${recoveryRunbookUrl}#fresh-host-step-2`,
    },
    {
      name: 'Project Git remotes', status: 'Not checked by Studio',
      step: 'Prepare read access to every remote', href: `${recoveryRunbookUrl}#fresh-host-step-3`,
    },
    {
      name: 'Stack and backup target settings', status: 'Archived configuration; new-host values require review',
      step: 'Compare restored configuration before start', href: `${recoveryRunbookUrl}#fresh-host-step-6`,
    },
    {
      name: 'Operations Server', status: 'Confirm this archive includes operations/; offline fencing required',
      step: 'Restore authority, re-enrol remote agents and check revocations', href: `${recoveryRunbookUrl}#fresh-host-step-8`,
    },
    {
      name: 'Quality Studio', status: 'Data and registry archived when present; image and repository checkouts required',
      step: 'Restore optional service', href: `${recoveryRunbookUrl}#fresh-host-step-9`,
    },
    {
      name: 'Owner account and second factor', status: 'Not checked by Studio',
      step: 'Sign in and review post-backup security changes', href: `${recoveryRunbookUrl}#fresh-host-step-10`,
    },
  ];
}

export function recoveryHandoff(overview: BackupOverview): string {
  const lines = [
    'Studio fresh-host recovery handoff',
    `Captured: ${overview.generatedAt}`,
    `Archive: ${overview.lastGood?.id ?? 'none'}`,
    `Archive SHA-256: ${overview.lastGood?.sha256 ?? 'unknown'}`,
    `Off-host target: ${overview.offHost.target ?? 'not configured'}`,
    'Do not start the stack before restore. Never submit escrowed keys to the live API.',
    '', 'Recovery inputs and last known checks:',
    ...recoveryInputs(overview).map(input => `${input.name}: ${input.status}\n  ${input.step}: ${input.href}`),
    '', 'Follow every step from setup through first sign-in and security review:',
    `${recoveryRunbookUrl}#restore-onto-a-new-host`,
  ];
  return lines.join('\n') + '\n';
}
