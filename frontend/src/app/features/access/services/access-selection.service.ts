import { Injectable, signal } from '@angular/core';

/** What a row of the Access page is. API tokens are service accounts with their own section. */
export type AccessDetailKind = 'user' | 'group' | 'service-account' | 'api-token' | 'enrollment' | 'runner';
/** The things the Access page can create; each opens its form in the detail area. */
export type AccessCreateKind = 'invite' | 'group' | 'service-account' | 'enrollment' | 'binding';

export type AccessSelection =
  | { mode: 'detail'; kind: AccessDetailKind; id: string }
  | { mode: 'create'; kind: AccessCreateKind };

/**
 * The one selection of the Access page: the row whose detail is open, or the
 * form that creates a new entry. Grids set it, the detail area reads it.
 * `armed` names the destructive action whose in-place confirmation is open,
 * so a row menu can lead straight to it.
 */
@Injectable()
export class AccessSelectionService {
  readonly selection = signal<AccessSelection | null>(null);
  readonly armed = signal<string | null>(null);

  open(kind: AccessDetailKind, id: string, armed: string | null = null): void {
    this.selection.set({ mode: 'detail', kind, id });
    this.armed.set(armed);
  }

  create(kind: AccessCreateKind): void {
    this.selection.set({ mode: 'create', kind });
    this.armed.set(null);
  }

  close(): void {
    this.selection.set(null);
    this.armed.set(null);
  }

  /** The selected row id when the open detail is of this kind, for a grid's `selectedId`. */
  selectedId(kind: AccessDetailKind): string | null {
    const selection = this.selection();
    return selection?.mode === 'detail' && selection.kind === kind ? selection.id : null;
  }

  setArmed(key: string, armed: boolean): void {
    if (armed) this.armed.set(key);
    else if (this.armed() === key) this.armed.set(null);
  }
}
