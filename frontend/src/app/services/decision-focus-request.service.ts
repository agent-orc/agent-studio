import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

/** How long an unanswered request may still move focus once the card renders. */
export const DECISION_FOCUS_HOLD_MS = 5_000;

/** The task key the board and the task tabs address a card by. */
export function decisionTaskKey(task: { taskKey?: string | null; watchPath?: string | null; id: string }): string {
  return task.taskKey || `${task.watchPath}::${task.id}`;
}

interface DecisionFocusRequest {
  readonly taskKey: string;
  readonly until: number;
}

/**
 * AGT-3833: a card opened to be decided on (from Needs you) asks for focus on
 * its decision area instead of the detail panes, so the primary action is one
 * Tab away. The request is answered by the first visible `appDecisionFocus`
 * area of that card and ends at the next key or pointer press, so a person who
 * has moved on is never pulled back.
 */
@Injectable({ providedIn: 'root' })
export class DecisionFocusRequestService {
  private readonly document = inject(DOCUMENT);
  private readonly pending = signal<DecisionFocusRequest | null>(null);
  private releaseListeners: (() => void) | null = null;

  /** Signal read for effects that answer the request. */
  readonly request = this.pending.asReadonly();

  ask(taskKey: string): void {
    this.clear();
    this.pending.set({ taskKey, until: Date.now() + DECISION_FOCUS_HOLD_MS });
    const end = () => this.clear();
    this.document.addEventListener('keydown', end, true);
    this.document.addEventListener('pointerdown', end, true);
    this.releaseListeners = () => {
      this.document.removeEventListener('keydown', end, true);
      this.document.removeEventListener('pointerdown', end, true);
    };
  }

  /** True while a request is open, for this card when `taskKey` is given. */
  wants(taskKey?: string): boolean {
    const request = this.pending();
    if (!request) return false;
    if (Date.now() > request.until) return false;
    return taskKey === undefined || request.taskKey === taskKey;
  }

  clear(): void {
    this.releaseListeners?.();
    this.releaseListeners = null;
    if (this.pending()) this.pending.set(null);
  }
}
