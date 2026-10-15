import { Injectable, signal } from '@angular/core';

/** Live Human Operator answers shared by the escalation headline and receipt controls. */
@Injectable({ providedIn: 'root' })
export class DecisionAskStateService {
  private readonly choices = signal<Record<string, string>>({});

  choice(taskId: string): string | null {
    return this.choices()[taskId] ?? null;
  }

  record(taskId: string, action: string): void {
    this.choices.update(current => ({ ...current, [taskId]: action }));
  }

  clear(taskId: string): void {
    this.choices.update(current => {
      const next = { ...current };
      delete next[taskId];
      return next;
    });
  }
}
