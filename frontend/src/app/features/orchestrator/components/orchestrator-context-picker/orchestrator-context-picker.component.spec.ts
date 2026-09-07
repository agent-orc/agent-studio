import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { OrchestratorContextSourceOption } from '../../models/orchestrator-context-source.model';
import { OrchestratorContextPickerComponent } from './orchestrator-context-picker.component';

const CURRENT: OrchestratorContextSourceOption = {
  id: 'page:Demo:page:Demo/concepts/context.md',
  category: 'current',
  key: 'CTX-W1',
  label: 'Context workspace',
  detail: 'Page · concepts/context.md',
  estimateTokens: 1_200,
  reference: { kind: 'page', reference: 'page:Demo/concepts/context.md', projectId: 'Demo' },
};

describe('OrchestratorContextPickerComponent', () => {
  async function fixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextPickerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const result = TestBed.createComponent(OrchestratorContextPickerComponent);
    result.componentRef.setInput('project', 'Demo');
    result.componentRef.setInput('automaticLabel', 'Context workspace with a deliberately long title');
    result.componentRef.setInput('currentSource', CURRENT);
    result.detectChanges();
    return result;
  }

  it('renders nothing until the composer asks for it', async () => {
    const result = await fixture();
    const root = result.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="orch-context-source-picker"]')).toBeNull();
    // The chips the picker used to draw now belong to the `<cac-chat>` chip row.
    expect(root.querySelector('[data-testid="orch-context-draft"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-add-context"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-context-estimate"]')).toBeNull();
  });

  it('prioritizes the current tab and emits its stable typed reference', async () => {
    const result = await fixture();
    const added = vi.fn();
    result.componentInstance.attachmentAdded.subscribe(added);

    result.componentInstance.show();
    result.detectChanges();
    const root = result.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-current-source"]')!.click();

    expect(added).toHaveBeenCalledWith(CURRENT);
  });

  it('marks an already attached source as added instead of offering a duplicate', async () => {
    const result = await fixture();
    result.componentRef.setInput('selectedIds', new Set([CURRENT.id]));
    result.componentInstance.show();
    result.detectChanges();

    const root = result.nativeElement as HTMLElement;
    const row = root.querySelector<HTMLButtonElement>('[data-testid="orch-context-current-source"]')!;
    expect(row.disabled).toBe(true);
    expect(row.textContent).toContain('Added');
  });

  it('offers a re-include row once the automatic block was dropped from the next message', async () => {
    const result = await fixture();
    const includedChange = vi.fn();
    result.componentInstance.automaticIncludedChange.subscribe(includedChange);
    result.componentRef.setInput('automaticIncluded', false);
    result.componentInstance.show();
    result.detectChanges();

    const root = result.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-current-automatic"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-include-automatic"]')!.click();

    expect(includedChange).toHaveBeenCalledWith(true);
  });

  it('reports the automatic block as already included while it is in scope', async () => {
    const result = await fixture();
    result.componentRef.setInput('currentSource', null);
    result.componentInstance.show();
    result.detectChanges();

    const root = result.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-include-automatic"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-context-current-automatic"]')?.textContent)
      .toContain('already included');
  });
});
