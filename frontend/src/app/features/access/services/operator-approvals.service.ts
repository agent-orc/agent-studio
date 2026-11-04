import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';
import type { OperatorApproval } from '../models/operator-approval.model';
import type { GrantStepUp } from './standing-grants.service';

const APPROVALS = '/api/auth/iam/operator-approvals';

type LoadState = 'idle' | 'loading' | 'ready' | 'denied' | 'error';
export type ApprovalDecision = 'approve' | 'reject';

/**
 * The Owner's approval requests (SM-5). Approving and rejecting each need a
 * fresh step-up, which this service runs right before the decision. The
 * server takes the deciding actor from the session; nothing here names it.
 * A read refusal hides the section.
 */
@Injectable({ providedIn: 'root' })
export class OperatorApprovalsService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  readonly approvals = signal<readonly OperatorApproval[]>([]);
  readonly state = signal<LoadState>('idle');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  async load(): Promise<void> {
    this.state.set('loading');
    this.error.set(null);
    try {
      this.approvals.set(await firstValueFrom(this.http.get<OperatorApproval[]>(APPROVALS)));
      this.state.set('ready');
    } catch (failure: unknown) {
      const status = failure instanceof HttpErrorResponse ? failure.status : 0;
      if (status === 401 || status === 403 || status === 404) {
        this.approvals.set([]);
        this.state.set('denied');
        return;
      }
      this.state.set('error');
      this.error.set(message(failure, 'Approval requests could not be loaded.'));
    }
  }

  async decide(approvalId: string, decision: ApprovalDecision, note: string, stepUp: GrantStepUp): Promise<OperatorApproval | null> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.verify(stepUp);
      const decided = await firstValueFrom(this.http.post<OperatorApproval>(
        `${APPROVALS}/${encodeURIComponent(approvalId)}/${decision}`, note ? { note } : {}));
      await this.load();
      return decided;
    } catch (failure: unknown) {
      this.error.set(message(failure, decision === 'approve' ? 'The approval was not recorded.' : 'The rejection was not recorded.'));
      await this.load();
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  private async verify(stepUp: GrantStepUp): Promise<void> {
    if ('passkey' in stepUp) {
      const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
      await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
    } else {
      await firstValueFrom(this.auth.stepUp(stepUp.code));
    }
  }
}

function message(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: unknown } | null;
    if (body && typeof body.message === 'string' && body.message) return body.message;
  }
  return fallback;
}
