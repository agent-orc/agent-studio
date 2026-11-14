import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WorkspaceOverlaysService } from '../../../shell';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import type { OperatorAttentionItem } from '../../../operator-attention';
import { AttentionControlComponent } from './attention-control';
import { CredentialInventoryService } from '../../../credentials/credential-inventory.service';

const items: OperatorAttentionItem[] = [
  { id: 'h1', type: 'human-review.ready', project: 'alpha', cardKey: 'AGT-2', cards: ['AGT-2'],
    reason: 'AGT-2 is ready for human review.', observedAt: '2026-11-09T09:00:00Z', suggestedOperations: [] },
  { id: 'd1', type: 'decision-card.opened', project: 'alpha', cardKey: 'AGT-3', cards: ['AGT-3'],
    reason: 'Which store?', observedAt: '2026-11-09T08:00:00Z', suggestedOperations: [] },
  { id: 's1', type: 'backend.stalled', project: null, cardKey: null, cards: [],
    reason: 'agent-runner-01 refuses claims.', observedAt: '2026-11-09T07:00:00Z', suggestedOperations: [] },
];

describe('AttentionControlComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function setup(backupState: 'ok' | 'disabled' = 'ok', dossierCount = 0) {
    const openReferenceOrNotify = vi.fn(() => true);
    const overlays = { open: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [AttentionControlComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } },
        { provide: WorkspaceOverlaysService, useValue: overlays },
      ],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const tabs = TestBed.inject(StudioTabStateService);
    const openTab = vi.spyOn(tabs, 'open').mockImplementation(() => undefined);
    const fixture = TestBed.createComponent(AttentionControlComponent);
    fixture.detectChanges();
    http.expectOne('/api/workbenches').flush({ items: dossierCount ? [{ projectName: 'alpha', workbench: {
      id: 'dossier-1', key: 'DOC-1', title: 'Concept Dossier', status: 'decision-pending',
      openDecisionCount: dossierCount, updatedAtUtc: '2026-11-09T09:00:00Z',
    } }] : [] });
    http.expectOne('/api/operator/backups').flush({ state: backupState, stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();
    return { fixture, http, openReferenceOrNotify, overlays, openTab };
  }

  function rows(): HTMLElement[] {
    return Array.from(document.querySelectorAll<HTMLElement>('[data-testid="attention-row"]'));
  }

  it('counts exactly the rows the list shows, grouped in class order', async () => {
    const { fixture, http } = await setup();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('3');

    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();

    expect(rows()).toHaveLength(3);
    const groups = Array.from(document.querySelectorAll('[data-testid^="attention-group-"]'))
      .map(group => group.getAttribute('data-testid'));
    expect(groups).toEqual(['attention-group-decision', 'attention-group-human-review', 'attention-group-host']);
    expect(rows().map(row => row.querySelector('[data-testid="attention-row-action"]')?.textContent?.trim()))
      .toEqual(['Decide', 'Review', 'Open hosts']);

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(rows()).toHaveLength(0);
  });

  it('adds backup and credential warnings to the visible installation group', async () => {
    const { fixture, http, overlays } = await setup('disabled');
    TestBed.inject(CredentialInventoryService).inventory.set({
      generatedAt: '2026-11-09T09:00:00Z',
      items: [{ id: 'provider', label: 'Provider token', state: 'expiring', detail: 'Expires soon.' } as never],
    });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('5');
    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/backups').flush({ state: 'disabled', stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();
    const group = document.querySelector('[data-testid="attention-group-installation"]') as HTMLElement;
    expect(group.querySelector('[data-testid="attention-count-in-group"]')?.textContent?.trim()).toBe('2');
    expect(group.textContent).toContain('Backups off');
    expect(group.textContent).toContain('Expires soon.');
    expect(group.querySelectorAll<HTMLButtonElement>('[data-testid="attention-row-action"]')[0].textContent).toContain('Open operations');
    const action = group.querySelectorAll<HTMLButtonElement>('[data-testid="attention-row-action"]')[1];
    expect(action.textContent).toContain('Open credentials');
    action.click();
    expect(overlays.open).toHaveBeenCalledWith('credentials');
  });

  it('adds open Dossier gates with their decision count and opens the Dossier', async () => {
    const { fixture, http, openTab } = await setup('ok', 2);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('4');
    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/backups').flush({ state: 'ok', stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();
    const group = document.querySelector('[data-testid="attention-group-dossier"]') as HTMLElement;
    expect(group.textContent).toContain('2 open decisions');
    (group.querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    expect(openTab).toHaveBeenCalledWith({ kind: 'workbench', projectName: 'alpha',
      workbenchId: 'dossier-1', title: 'Concept Dossier', key: 'DOC-1' });
  });

  it('opens the card of a review row and the hosts surface of a host row', async () => {
    const { fixture, http, openReferenceOrNotify, overlays, openTab } = await setup();
    const trigger = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement;

    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();
    (rows()[1].querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-2');
    expect(rows()).toHaveLength(0);

    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    await fixture.whenStable();
    (rows()[2].querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    expect(overlays.open).toHaveBeenCalledWith('remote-hosts');
    expect(openTab).toHaveBeenCalledWith({ kind: 'workspace-settings' });
  });

  it('says so when nothing needs the operator', async () => {
    await TestBed.configureTestingModule({
      imports: [AttentionControlComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AttentionControlComponent);
    fixture.detectChanges();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-list-empty"]')?.textContent).toContain('Nothing needs you');
    expect(rows()).toHaveLength(0);
  });
});
