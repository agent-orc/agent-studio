import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { OperatorApprovalsService } from '../../services/operator-approvals.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { approvalIdFromHash } from '../../models/operator-approval.model';
import { fakeOperatorApprovalsService, operatorApproval } from '../../testing/approval-fixtures';
import { AccessOperatorApprovalsComponent } from './access-operator-approvals.component';

describe('AccessOperatorApprovalsComponent', () => {
  let approvals: ReturnType<typeof fakeOperatorApprovalsService>;
  let fixture: ComponentFixture<AccessOperatorApprovalsComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    approvals = fakeOperatorApprovalsService([
      operatorApproval(),
      operatorApproval({
        id: 'apr_web', operation: 'secrets.change', operationTitle: 'Change secrets', method: 'POST',
        path: '/api/projects/PROJ-001/secrets', query: '', parameters: '{\n  "name": "DEPLOY_VALUE",\n  "value": "[masked]"\n}',
        instructionSource: 'web-page', reason: null, requestedAt: '2026-11-04T09:05:00Z', expiresAt: '2026-11-04T09:20:00Z',
      }),
      operatorApproval({
        id: 'apr_done', state: 'used', decidedBy: 'usr_owner', decidedAt: '2026-11-04T08:40:00Z', usedAt: '2026-11-04T08:41:00Z',
        resultStatus: 200, requestedAt: '2026-11-04T08:30:00Z',
      }),
    ]);
    window.location.hash = '#/workspace/settings/access?approval=apr_web';
    await TestBed.configureTestingModule({
      imports: [AccessOperatorApprovalsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OperatorApprovalsService, useValue: approvals },
        { provide: AccessService, useValue: fakeAccessService() },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessOperatorApprovalsComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => { window.location.hash = ''; });

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;

  it('shows each waiting request with request line, parameters, digest, target, session, source and reason', () => {
    const waiting = byTestId('access-approval-apr_wait')!.textContent ?? '';
    expect(waiting).toContain('T2 high risk');
    expect(waiting).toContain('Delete data');
    expect(byTestId('access-approval-request-apr_wait')?.textContent).toBe('DELETE /api/tasks/AGT-12?project=PROJ-001');
    expect(byTestId('access-approval-digest-apr_wait')?.textContent).toBe(`sha256:${'4c1d'.repeat(16)}`);
    expect(waiting).toContain('project:PROJ-001/task:AGT-12');
    expect(waiting).toContain('operator-agent:opa_ops/session:ses_morning');
    expect(byTestId('access-approval-source-apr_wait')?.textContent).toBe('Owner chat');
    expect(byTestId('access-approval-reason-apr_wait')?.textContent).toBe('Remove the duplicate card');
    expect(byTestId('access-approval-parameters-apr_web')?.textContent).toContain('"value": "[masked]"');
    expect(byTestId('access-approval-reason-apr_web')?.textContent).toBe('Not stated');
  });

  it('marks content sources as untrusted and the owner chat as trusted', () => {
    expect(byTestId('access-approval-source-apr_web')?.getAttribute('data-tone')).toBe('untrusted');
    expect(byTestId('access-approval-source-apr_web')?.textContent).toBe('Web page');
    expect(byTestId('access-approval-source-apr_wait')?.getAttribute('data-tone')).toBeNull();
  });

  it('lists waiting requests oldest first and focuses the linked one', () => {
    const order = Array.from(element.querySelectorAll('.approvals__pending > li')).map(item => item.getAttribute('data-approval-id'));
    expect(order).toEqual(['apr_wait', 'apr_web']);
    expect(byTestId('access-approval-apr_web')?.getAttribute('data-focused')).toBe('true');
    expect(byTestId('access-approval-apr_wait')?.getAttribute('data-focused')).toBeNull();
  });

  it('approves with the entered code and a note, then clears the code', async () => {
    const code = String(100_000 + Math.floor(Math.random() * 899_999));
    const input = byTestId<HTMLInputElement>('access-approval-code')!;
    input.value = code;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    const note = byTestId<HTMLInputElement>('access-approval-note-apr_wait')!;
    note.value = 'Duplicate confirmed';
    note.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    byTestId<HTMLButtonElement>('access-approval-approve-apr_wait')!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(approvals.decide).toHaveBeenCalledWith('apr_wait', 'approve', 'Duplicate confirmed', { code });
    expect(byTestId<HTMLInputElement>('access-approval-code')?.value).toBe('');
  });

  it('rejects with a passkey when no code is entered', async () => {
    byTestId<HTMLButtonElement>('access-approval-reject-apr_web')!.click();
    await fixture.whenStable();
    expect(approvals.decide).toHaveBeenCalledWith('apr_web', 'reject', '', { passkey: true });
  });

  it('keeps decided requests in a quiet history with their result', () => {
    const row = byTestId('access-approval-apr_done')!;
    expect(row.getAttribute('data-state')).toBe('used');
    expect(row.textContent).toContain('Executed');
    expect(row.textContent).toContain('Ran, answered 200');
    expect(row.textContent).toContain('Owner');
    expect(byTestId('access-approval-approve-apr_done')).toBeNull();
  });

  it('says so when nothing is waiting, and what became of a linked request', () => {
    approvals.approvals.set([operatorApproval({ id: 'apr_done', state: 'rejected', decidedAt: '2026-11-04T08:40:00Z', decisionNote: 'Not needed' })]);
    window.location.hash = '#/workspace/settings/access?approval=apr_done';
    window.dispatchEvent(new HashChangeEvent('hashchange'));
    fixture.detectChanges();
    expect(byTestId('access-approvals-empty')).not.toBeNull();
    expect(byTestId('access-approval-code')).toBeNull();
    expect(byTestId('access-approval-linked')?.textContent).toContain('rejected: Delete data, Not needed');
  });

  it('reads the linked approval from the hash route and ignores other segments', () => {
    expect(approvalIdFromHash('#/workspace/settings/access?approval=apr_x')).toBe('apr_x');
    expect(approvalIdFromHash('#/workspace/settings/access?approval=apr_x&filters=lane%3Dready')).toBe('apr_x');
    expect(approvalIdFromHash('#/workspace/settings/access')).toBeNull();
    expect(approvalIdFromHash('#filters=approval%3Dapr_x')).toBeNull();
  });
});
