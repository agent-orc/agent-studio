import { provideZonelessChangeDetection } from '@angular/core';
import { DeferBlockState, TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { WorkspaceQuotaListComponent } from './workspace-quota-list';

describe('WorkspaceQuotaListComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    // The editor sits in a @defer block, so its metadata resolves asynchronously.
    await TestBed.configureTestingModule({
      imports: [WorkspaceQuotaListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('opens one workspace quota editor at a time and closes it on a second click', async () => {
    const fixture = TestBed.createComponent(WorkspaceQuotaListComponent);
    fixture.componentRef.setInput('workspaces', [{ id: 'ws-a', displayName: 'Alpha' }, { id: 'ws-b', displayName: 'Beta' }]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const toggle = (id: string) => root.querySelector<HTMLButtonElement>(`[data-testid="settings-workspace-quota-${id}"]`)!;
    const open = async (id: string) => {
      toggle(id).click();
      await fixture.whenStable();
      await renderDeferred(fixture);
    };
    expect(toggle('ws-a').textContent?.trim()).toBe('Alpha quota');
    expect(toggle('ws-a').getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('app-workspace-quota')).toBeNull();

    await open('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota');
    expect(toggle('ws-a').getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelectorAll('app-workspace-quota')).toHaveLength(1);

    await open('ws-b');
    http.expectOne('/api/workspaces/ws-b/quota');
    expect(toggle('ws-a').getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelectorAll('app-workspace-quota')).toHaveLength(1);

    toggle('ws-b').click();
    await fixture.whenStable();
    expect(root.querySelector('app-workspace-quota')).toBeNull();
  });
});

async function renderDeferred(fixture: ComponentFixture<unknown>): Promise<void> {
  for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
}
