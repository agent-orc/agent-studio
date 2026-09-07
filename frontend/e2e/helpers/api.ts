/**
 * Tiny REST client for the backend.
 * Defaults to the dev backend at http://localhost:5030. Agents driving the
 * stable stack set `PW_TARGET=stable` (-> http://localhost:5031) or pin an
 * explicit URL via `PW_BACKEND_URL=http://...`. Same precedence table as
 * playwright.config.ts so a single env var flips both layers.
 *
 * Used by Playwright specs for setup / teardown / polling so we don't have
 * to drive the UI for things the API can do directly.
 */

const apiTarget = (process.env.PW_TARGET ?? 'dev').toLowerCase();
export const BACKEND =
  process.env.PW_BACKEND_URL?.trim()
  || (apiTarget === 'stable' ? 'http://localhost:5031' : 'http://localhost:5030');

/** Default identity used by Playwright specs when no override is given.
 * The bootstrap "local-default" identity exists on every backend boot,
 * so signing in as it lets specs perform mutations without registering
 * a per-test identity. Override with PW_CLIENT_ID for multi-client specs. */
const DEFAULT_CLIENT_ID = process.env.PW_CLIENT_ID?.trim() || 'local-default';

export async function api<T = unknown>(
  path: string,
  init: RequestInit = {}
): Promise<T> {
  const res = await fetch(`${BACKEND}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': DEFAULT_CLIENT_ID,
      ...(init.headers ?? {})
    },
    ...init
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${res.status} ${res.statusText}\n${text}`);
  }
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

/**
 * Teardown for e2e-registered client identities (AGT-2748). Every fixture
 * that registers a client with an `e2e-` prefix should call this from its
 * `afterAll` instead of hand-rolling a retire loop: it retires any live
 * match still standing, then permanently deletes everything under the
 * prefix that is already retired via `POST /api/clients/retired/purge`, so
 * leftovers stop accumulating in the shared dev workspace across runs. Best
 * effort throughout - a failed sweep here must never fail the test run;
 * the next spec's purge call sweeps whatever this one missed.
 */
export async function purgeE2eClients(prefix: string): Promise<void> {
  interface ClientSummary { id: string; kind: string; }
  let clients: ClientSummary[];
  try {
    clients = await api<ClientSummary[]>('/api/clients/');
  } catch {
    return;
  }
  for (const client of clients) {
    if (!client.id.startsWith(prefix) || client.kind === 'retired') continue;
    try { await api(`/api/clients/${encodeURIComponent(client.id)}`, { method: 'DELETE' }); } catch { /* ignore */ }
  }
  try {
    await api('/api/clients/retired/purge', {
      method: 'POST',
      body: JSON.stringify({ prefix, dryRun: false })
    });
  } catch { /* ignore: leftover identities get swept by the next purge */ }
}
