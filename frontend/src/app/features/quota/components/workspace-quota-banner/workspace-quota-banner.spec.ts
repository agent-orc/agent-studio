import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';

import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';
import { WorkspaceQuotaBannerComponent } from './workspace-quota-banner';

describe('WorkspaceQuotaBannerComponent', () => {
  it('shows the soft warning and the hard stop, and hides normal use and an active override', async () => {
    TestBed.configureTestingModule({
      imports: [WorkspaceQuotaBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(WorkspaceQuotaBannerComponent);
    const board = TestBed.inject(WorkspaceQuotaBoardState);
    const root = fixture.nativeElement as HTMLElement;
    const status = () => root.querySelector('[data-testid="workspace-quota-status"]');
    fixture.detectChanges();
    expect(status()).toBeNull();

    board.workspaceQuota.set({ status: 'warning', message: 'Workspace A used 85 % of its weekly model tokens.', overrideActive: false, codingAllowed: true });
    await fixture.whenStable();
    expect(status()?.getAttribute('data-status')).toBe('warning');
    expect(status()?.getAttribute('role')).toBe('status');
    expect(status()?.textContent).toContain('85 %');

    board.workspaceQuota.set({ status: 'blocked', message: 'Workspace A reached its weekly model-token quota.', overrideActive: false, codingAllowed: false });
    await fixture.whenStable();
    expect(status()?.getAttribute('data-status')).toBe('blocked');

    board.workspaceQuota.set({ status: 'blocked', message: 'Workspace A reached its weekly model-token quota.', overrideActive: true, codingAllowed: true });
    await fixture.whenStable();
    expect(status()).toBeNull();

    board.workspaceQuota.set({ status: 'ok', message: null, overrideActive: false, codingAllowed: true });
    await fixture.whenStable();
    expect(status()).toBeNull();
  });
});
