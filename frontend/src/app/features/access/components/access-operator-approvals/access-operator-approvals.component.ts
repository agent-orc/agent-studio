import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal } from '@angular/core';
import { formatAccessTime } from '../../models/access.model';
import {
  approvalIdFromHash,
  approvalResult,
  approvalStateLabel,
  instructionSourceLabel,
  isUntrustedSource,
  orderedApprovals,
  requestLine,
  type OperatorApproval,
} from '../../models/operator-approval.model';
import { tierLabel } from '../../models/standing-grant.model';
import { AccessService } from '../../services/access.service';
import { OperatorApprovalsService, type ApprovalDecision } from '../../services/operator-approvals.service';
import type { GrantStepUp } from '../../services/standing-grants.service';

/**
 * Owner approval of T2 operator requests (SM-5, decision SM-D8 option A).
 * Each waiting request shows the operation, the exact request and its
 * parameters, the parameter digest the approval binds, the target, the
 * session, where the instruction came from, and the agent's reason. Approving
 * or rejecting asks for a fresh second factor. A push link
 * (`#/workspace/settings/access?approval=<id>`) opens this list with that
 * request in focus.
 */
@Component({
  selector: 'app-access-operator-approvals',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-operator-approvals.component.html',
  styleUrl: './access-operator-approvals.component.scss',
})
export class AccessOperatorApprovalsComponent {
  readonly approvalsService = inject(OperatorApprovalsService);
  private readonly access = inject(AccessService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly time = formatAccessTime;
  readonly tierText = tierLabel;
  readonly sourceText = instructionSourceLabel;
  readonly untrusted = isUntrustedSource;
  readonly stateText = approvalStateLabel;
  readonly resultText = approvalResult;
  readonly line = requestLine;

  readonly code = signal('');
  readonly notes = signal<Readonly<Record<string, string>>>({});
  /** The request a push or feed link points at; the shell routes by hash, so the hash is the source. */
  readonly focused = signal<string | null>(approvalIdFromHash(window.location.hash));

  readonly ordered = computed(() => orderedApprovals(this.approvalsService.approvals()));
  readonly pending = computed(() => this.ordered().pending);
  readonly decided = computed(() => this.ordered().decided);
  /** The linked request when it is no longer waiting, so the Owner sees what became of it. */
  readonly focusedDecided = computed(() => {
    const id = this.focused();
    return id ? this.decided().find(approval => approval.id === id) ?? null : null;
  });

  constructor() {
    const follow = () => this.focused.set(approvalIdFromHash(window.location.hash));
    window.addEventListener('hashchange', follow);
    inject(DestroyRef).onDestroy(() => window.removeEventListener('hashchange', follow));
    effect(() => {
      const id = this.focused();
      if (!id || !this.approvalsService.approvals().some(approval => approval.id === id)) return;
      queueMicrotask(() => Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-approval-id]'))
        .find(item => item.dataset['approvalId'] === id)?.scrollIntoView?.({ block: 'center' }));
    });
  }

  /** A user id as its display name; unknown values stay as they are. */
  person(value: string | null | undefined): string {
    if (!value) return '';
    const user = this.access.overview()?.users.find(candidate => candidate.id === value || candidate.username === value);
    return user?.displayName ?? value;
  }

  note(approval: OperatorApproval): string {
    return this.notes()[approval.id] ?? '';
  }

  setNote(approval: OperatorApproval, value: string): void {
    this.notes.update(current => ({ ...current, [approval.id]: value }));
  }

  async decide(approval: OperatorApproval, decision: ApprovalDecision): Promise<void> {
    if (this.approvalsService.busy()) return;
    const decided = await this.approvalsService.decide(approval.id, decision, this.note(approval).trim(), this.stepUp());
    this.code.set('');
    if (decided) this.notes.update(current => Object.fromEntries(Object.entries(current).filter(([id]) => id !== approval.id)));
  }

  /** An entered authenticator or recovery code, otherwise a passkey. */
  private stepUp(): GrantStepUp {
    const code = this.code().trim();
    return code ? { code } : { passkey: true };
  }
}
