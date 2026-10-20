import { Injectable, signal } from '@angular/core';

/** Short-lived handoff when a marker in the Operations rail opens Deploy. */
@Injectable({ providedIn: 'root' })
export class OperationsReleaseSelectionService {
  readonly releaseId = signal<string | null>(null);
  select(id: string): void { this.releaseId.set(id); }
  consume(): void { this.releaseId.set(null); }
}
