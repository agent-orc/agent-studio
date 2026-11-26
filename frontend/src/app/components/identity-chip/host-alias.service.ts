import { Injectable, signal } from '@angular/core';
import { hostDisplay, type HostDisplay } from './identity-display';

const STORAGE_KEY = 'studio.hostAliases.v1';

/**
 * Owner renames for hosts (AGT-4034). Kept in this browser; the generated alias
 * is derived from the id, so it needs no storage and never changes.
 */
@Injectable({ providedIn: 'root' })
export class HostAliasService {
  private readonly overrides = signal<Readonly<Record<string, string>>>(read());

  /** Reads the signal, so templates and computeds update after a rename. */
  display(hostId: string | null | undefined, name?: string | null, role?: string | null): HostDisplay {
    return hostDisplay(hostId, { name, role, override: hostId ? this.overrides()[hostId] : null });
  }

  rename(hostId: string, alias: string): void {
    const next = { ...this.overrides() };
    const value = alias.trim();
    if (value) next[hostId] = value; else delete next[hostId];
    this.overrides.set(next);
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(next)); } catch { /* storage unavailable */ }
  }

  override(hostId: string): string | null { return this.overrides()[hostId] ?? null; }
}

function read(): Record<string, string> {
  try {
    const parsed = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch { return {}; }
}
