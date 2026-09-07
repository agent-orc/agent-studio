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

  it('stays popover-only until the host asks it to open', async () => {
    const result = await fixture();
    const root = result.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="orch-context-draft"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-context-source-picker"]')).toBeNull();
    result.componentInstance.show();
    result.detectChanges();
    expect(root.querySelector('[data-testid="orch-context-source-picker"]')).not.toBeNull();
  });
});
