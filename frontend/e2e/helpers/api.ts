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

/** Retire and permanently delete e2e identities so shared workspaces stay clean. */
export async function cleanupE2eClients(prefix = 'e2e-'): Promise<void> {
  const clients = await api<Array<{ id: string; displayName: string; kind: string }>>('/api/clients/');
  for (const client of clients.filter(item =>
    item.id.startsWith(prefix) || item.displayName.startsWith(prefix))) {
    try {
      if (client.kind !== 'retired') {
        await api(`/api/clients/${encodeURIComponent(client.id)}/retire`, { method: 'POST', body: '{}' });
      }
      await api(`/api/clients/${encodeURIComponent(client.id)}`, { method: 'DELETE' });
    } catch { /* best-effort teardown; guarded identities remain visible for an operator */ }
  }
}
