import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ConfirmDialogService } from '../../../../../services/confirm-dialog.service';
import type { PipelineAdminRow } from '../pipeline-config.util';
import { AspectModelBulkApplyComponent, type AspectAgentSelection } from './aspect-model-bulk-apply.component';

function step(id: string, overrides: Partial<PipelineAdminRow> = {}): PipelineAdminRow {
  return {
    id, displayName: id, kind: 'aspect', usesModel: true, effectiveCliType: 'claude',
    effectiveModel: 'claude-opus-5', effectiveThinkingLevel: 'high', model: '', overridesWorkspace: false,
    ...overrides,
  } as PipelineAdminRow;
}

async function setup(confirmed = true) {
  const confirm = vi.fn(async () => confirmed);
  await TestBed.configureTestingModule({
    imports: [AspectModelBulkApplyComponent],
    providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: ConfirmDialogService, useValue: { confirm } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(AspectModelBulkApplyComponent);
  const applied: AspectAgentSelection[] = [];
  fixture.componentRef.setInput('steps', [
    step('aspect-code-quality'), step('aspect-security', { overridesWorkspace: true, model: 'gpt-6-sol' }),
  ]);
  fixture.componentRef.setInput('applyToSteps', (selection: AspectAgentSelection) => { applied.push(selection); return of(null); });
  fixture.detectChanges();
  return { fixture, applied, confirm, http: TestBed.inject(HttpTestingController) };
}

describe('AspectModelBulkApplyComponent', () => {
  it('starts from the first aspect route and applies it to every aspect step', async () => {
    const { fixture, applied } = await setup();
    const component = fixture.componentInstance;
    expect(component.selection()).toEqual({ cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' });
    component.onSelect({ cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'medium' });
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="aspect-model-apply-steps"]')?.click();
    expect(applied).toEqual([{ cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'medium' }]);
    expect(component.message()?.text).toContain('set on 2 aspect steps');
  });

  it('applies to all projects through the review-aspect consumer policy after confirmation', async () => {
    const { fixture, confirm, http } = await setup();
    let emitted = 0;
    fixture.componentInstance.policyApplied.subscribe(() => emitted++);
    fixture.componentInstance.onSelect({ cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' });
    await fixture.componentInstance.applyToAllProjects();
    expect(confirm).toHaveBeenCalledOnce();
    http.expectOne('/api/model-selection/policy').flush({
      consumers: { 'review-aspect': { primary: null, route: null, fallbacks: 'catalogue', sameClassOnly: true,
        stickyWithinCard: true, costGuardFactor: null } },
    });
    const put = http.expectOne('/api/model-selection/policy/consumers/review-aspect');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toMatchObject({
      primary: { cli: 'codex', model: 'gpt-6-sol', thinking: 'high' }, fallbacks: 'catalogue', sameClassOnly: true,
    });
    put.flush({});
    expect(emitted).toBe(1);
    expect(fixture.componentInstance.message()?.text).toContain('1 steps in this project keep their override');
    http.verify();
  });

  it('does not write the policy when the operator cancels', async () => {
    const { fixture, http } = await setup(false);
    await fixture.componentInstance.applyToAllProjects();
    http.expectNone('/api/model-selection/policy');
    expect(fixture.componentInstance.busy()).toBeNull();
  });
});
