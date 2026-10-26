import { provideZonelessChangeDetection } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';

import { WorkspaceQuotaDisclosureComponent } from './workspace-quota-disclosure';

describe('WorkspaceQuotaDisclosureComponent', () => {
  it('keeps the editor unloaded until the operator opens the row', async () => {
    TestBed.configureTestingModule({
      imports: [WorkspaceQuotaDisclosureComponent],
      deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    // The @defer block resolves the editor dependency asynchronously.
    await TestBed.compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WorkspaceQuotaDisclosureComponent);
    fixture.componentRef.setInput('workspaceId', 'ws-a');
    fixture.componentRef.setInput('workspaceName', 'Workspace A');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLButtonElement>('[data-testid="settings-workspace-quota-ws-a"]')!;

    expect(toggle.textContent).toContain('Workspace A quota');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.querySelector('app-disclosure-marker')).not.toBeNull();
    expect(root.querySelector('app-workspace-quota')).toBeNull();
    http.expectNone('/api/workspaces/ws-a/quota');

    toggle.click();
    await fixture.whenStable();
    const [editor] = await fixture.getDeferBlocks();
    await editor.render(DeferBlockState.Complete);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector('app-workspace-quota')).not.toBeNull();
    http.expectOne('/api/workspaces/ws-a/quota');
  });
});
