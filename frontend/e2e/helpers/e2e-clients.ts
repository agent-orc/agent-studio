import { api } from './api';

/**
 * Shared helper for Playwright specs that register throwaway client
 * identities (owners, runners) against the real backend.
 *
 * AGT-2748: retired e2e identities used to accumulate forever - each spec
 * minted a uniquely-timestamped id (`e2e-effective-model-default-<ts>`) and
 * only soft-deleted it on teardown, so every run left one more retired
 * "machine" behind on Execution Hosts. Historical attribution is worth
 * preserving for real owners; it is not worth preserving for a fixture that
 * exists for the duration of one test run. Every id minted through
 * {@link registerE2eClient} carries the `e2e-` prefix so a spec whose
 * teardown never ran (crash, interrupted run) can still be swept later via
 * the "Delete retired..." purge dialog's `e2e-` prefix default.
 */

export interface ClientSummary {
  id: string;
  displayName: string;
  kind: string;
  defaultCliType?: string | null;
  defaultModel?: string | null;
  emoji?: string | null;
  colour?: string | null;
}

export const E2E_CLIENT_PREFIX = 'e2e-';

/** Registers a client identity, prefixing its display name with `e2e-` if the caller did not already. */
export async function registerE2eClient(
  displayName: string,
  opts: { emoji?: string; colour?: string; kind?: string } = {},
): Promise<ClientSummary> {
  const prefixed = displayName.startsWith(E2E_CLIENT_PREFIX) ? displayName : `${E2E_CLIENT_PREFIX}${displayName}`;
  return api<ClientSummary>('/api/clients/register', {
    method: 'POST',
    body: JSON.stringify({
      displayName: prefixed,
      emoji: opts.emoji ?? '🧪',
      colour: opts.colour ?? '#7c3aed',
      kind: opts.kind ?? 'human',
    }),
  });
}

/**
 * Retires then permanently deletes every registered client whose id starts
 * with `prefix` (default: every `e2e-` identity). Call from `test.afterAll`
 * so ephemeral fixtures never linger as retired history. Best-effort per
 * client: a client the delete guard refuses (still online, an active lease,
 * or an unresolved attempt) is left retired for a later "Delete retired..."
 * purge sweep instead of failing the whole teardown.
 */
export async function cleanupE2eClients(prefix: string = E2E_CLIENT_PREFIX): Promise<void> {
  const all = await api<ClientSummary[]>('/api/clients/');
  for (const client of all) {
    if (!client.id.startsWith(prefix)) continue;
    try {
      if (client.kind !== 'retired') {
        await api(`/api/clients/${client.id}/retire`, { method: 'POST' });
      }
      await api(`/api/clients/${client.id}/permanent`, { method: 'DELETE' });
    } catch {
      // Left retired; a later "Delete retired..." purge sweep can clean it up.
    }
  }
}
