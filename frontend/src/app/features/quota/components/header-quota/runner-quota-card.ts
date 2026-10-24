import type { CliType } from '../../../../models/task.model';
import { cliTypeIcon } from '../../../../services/format.util';
import type { ProviderAuthBadge } from '../../../remote-hosts/models/provider-auth.model';
import type { QuotaCardModel } from './header-quota';

/** Host capability badges are the networked status source; they carry no usage percentage. */
export function runnerQuotaCard(
  cliType: CliType, label: string, badges: readonly ProviderAuthBadge[],
): QuotaCardModel {
  const state = badges.some(badge => badge.state === 'unavailable') ? 'unavailable'
    : badges.some(badge => badge.state === 'limited') ? 'warn'
      : badges.some(badge => badge.state === 'ok') ? 'idle' : 'stale';
  return {
    cliType, icon: cliTypeIcon(cliType), label,
    ariaLabel: badges.length
      ? `${label} runner hosts: ${badges.map(badge => `${badge.hostName} ${badge.state}; accepted models: ${badge.acceptedModels?.join(', ') || 'none verified'}`).join(', ')}`
      : `${label} runner host state unknown`,
    chips: badges.map(badge => ({
      windowKey: badge.runnerId, tag: badge.hostName,
      value: `${badge.state} · ${badge.acceptedModels?.join(', ') || 'no models verified'}`, barPct: 0,
      tone: badge.state === 'ok' ? 'ok' : badge.state === 'limited' ? 'warn' : 'unknown',
    })),
    tone: state === 'idle' ? 'ok' : state === 'warn' ? 'warn' : 'unknown',
    state, fetchedAt: null, stale: false, freshness: 'runner host status', windows: [],
    error: null, probeFailureLabel: null, source: 'runner-host',
  };
}
