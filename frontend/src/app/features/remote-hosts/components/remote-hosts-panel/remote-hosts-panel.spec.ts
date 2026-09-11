import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { ReviewQueueService, type ReviewQueueSnapshot } from '../../services/review-queue.service';

function reviewSnapshot(overrides: Partial<ReviewQueueSnapshot> = {}): ReviewQueueSnapshot {
  return {
    queueDepth: 3,
    activeJobs: 2,
    isStagnant: false,
    stagnantSince: null,
    stagnantThresholdMinutes: 20,
    drainRatePerMinute: 1.4,
    medianReviewDurationMs: 190_000,
    throughputWindowMinutes: 60,
    observedAt: '2026-08-17T12:00:00Z',
    ...overrides,
  };
}

/**
 * Render-path test: the panel seeds its registry on init and renders one table
 * row per host with a summary line whose counts reconcile to the visible rows
 * (R3 sum invariant).
 */
describe('RemoteHostsPanelComponent', () => {
  it('mounts, seeds the registry, and renders a sortable row per host', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-panel"]')).toBeTruthy();
    expect(el.querySelector('h2')?.textContent).toContain('Execution Hosts');
    const cards = el.querySelectorAll('[data-testid="remote-host-card"]');
    expect(cards.length).toBe(fixture.componentInstance.total());
    expect(cards.length).toBeGreaterThanOrEqual(2);
    const local = el.querySelector('[data-host="local"]');
    expect(local?.querySelector('[data-testid="remote-host-name"]')?.textContent)
      .toContain('Local machine');
    expect(el.querySelector('[data-testid="remote-hosts-table"]')).toBeTruthy();

    // Summary total equals the number of rendered cards (R3).
    const summary = el.querySelector('[data-testid="remote-hosts-summary"]')?.textContent ?? '';
    expect(summary).toContain(String(cards.length));

    // The setup control now lives inside the disclosed "Connection" detail
    // section: the reworked grouped table (AGT-2653/2681) opens a machine row to
    // its compact section summaries (AGT-2629), then the connection section
    // reveals the per-host actions. Expand the row, open its connection section,
    // then reach the setup control.
    const remote = el.querySelector('[data-host="agent-runner-01"]') as HTMLElement;
    (remote.querySelector('[data-testid="remote-host-disclosure"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (remote.querySelector('[data-testid="remote-host-detail-toggle-connection"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const setupButton = remote.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement;
    setupButton.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.setupHost()?.id).toBe('agent-runner-01');
    expect(el.querySelector('[data-testid="runner-setup-dialog"]')).toBeTruthy();
    fixture.componentInstance.closeSetup();
    fixture.detectChanges();

    const addButton = el.querySelector('[data-testid="remote-hosts-add"]') as HTMLButtonElement;
    addButton.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="add-host-wizard"]')).toBeTruthy();
    expect(el.querySelector('#add-host-title')?.textContent).toContain('Add an execution host');

    fixture.destroy();
  });

  it('renders the corrupt identity recovery diagnostic', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    service.identityDiagnostics.set([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'service', registeredAt: '2026-08-05T14:35:00Z', lastSeenAt: null,
      tokenBudgetMonthly: null, notes: null,
      identityFileError: 'identity file corrupt: agent-runner-01.json',
      identityFileName: 'agent-runner-01.json',
      identityFileModifiedAt: '2026-08-05T14:35:00Z',
      identityRestoreHint: 'Restore a valid file or re-register with POST /api/clients/register.',
    }]);

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const diagnostic = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="remote-hosts-identity-errors"]',
    );

    expect(diagnostic?.textContent).toContain('identity file corrupt: agent-runner-01.json');
    expect(diagnostic?.textContent).toContain('POST /api/clients/register');
    fixture.destroy();
  });

  it('shows the toolbar "Delete retired…" action only once a retired host exists, and opens its dry-run dialog', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-purge-retired"]')).toBeNull();

    const service = TestBed.inject(RemoteHostsService);
    service.hosts.update(hosts => hosts.map(host =>
      host.id === 'agent-runner-01' ? { ...host, status: 'retired' as const } : host));
    fixture.detectChanges();

    const purgeButton = el.querySelector('[data-testid="remote-hosts-purge-retired"]') as HTMLButtonElement;
    expect(purgeButton).toBeTruthy();
    purgeButton.click();
    fixture.detectChanges();
    // <app-dialog> portals itself onto <body>, so the rendered dialog is no
    // longer a descendant of the panel's own root element.
    expect(document.querySelector('[data-testid="purge-retired-dialog"]')).toBeTruthy();

    fixture.componentInstance.closePurgeRetired();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="purge-retired-dialog"]')).toBeNull();
    fixture.destroy();
  });

  it('routes a role-row delete action through the name-bearing confirmation modal', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();

    fixture.componentInstance.onAction({ kind: 'delete', id: 'agent-runner-01' });
    fixture.detectChanges();

    const pending = fixture.componentInstance.pendingConfirmation();
    expect(pending?.kind).toBe('delete');
    expect(fixture.componentInstance.confirmationTitle()).toContain('agent-runner-01');
    expect(fixture.componentInstance.confirmationText()).toContain('cannot be undone');
    fixture.destroy();
  });

  describe('auto-review queue summary', () => {
    async function mountWithReviewSnapshot(snapshot: ReviewQueueSnapshot | null) {
      await TestBed.configureTestingModule({
        imports: [RemoteHostsPanelComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const reviewQueue = TestBed.inject(ReviewQueueService);
      reviewQueue.snapshot.set(snapshot);
      const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
      fixture.detectChanges();
      return fixture;
    }

    it('renders the normal state (cards waiting, actively draining)', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot());
      const el: HTMLElement = fixture.nativeElement;
      const summary = el.querySelector('[data-testid="auto-review-queue-summary"]') as HTMLElement;

      expect(summary).toBeTruthy();
      expect(summary.classList.contains('rh__review--attention')).toBe(false);
      expect(el.querySelector('[data-testid="auto-review-queue-depth"]')?.textContent).toContain('3');
      expect(el.querySelector('[data-testid="auto-review-queue-active"]')?.textContent).toContain('2');
      expect(el.querySelector('[data-testid="auto-review-queue-drain-rate"]')?.textContent).toContain('1.4/min');
      expect(el.querySelector('[data-testid="auto-review-queue-duration"]')?.textContent).toContain('3m 10s');
      expect(el.querySelector('[data-testid="auto-review-queue-attention"]')).toBeFalsy();
      fixture.destroy();
    });

    it('renders the idle state (empty queue)', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot({
        queueDepth: 0,
        activeJobs: 0,
        drainRatePerMinute: 0,
        medianReviewDurationMs: null,
      }));
      const el: HTMLElement = fixture.nativeElement;

      expect(el.querySelector('[data-testid="auto-review-queue-depth"]')?.textContent).toContain('0');
      expect(el.querySelector('[data-testid="auto-review-queue-active"]')?.textContent).toContain('0');
      expect(el.querySelector('[data-testid="auto-review-queue-duration"]')?.textContent).toContain('-');
      expect(el.querySelector('[data-testid="auto-review-queue-attention"]')).toBeFalsy();
      fixture.destroy();
    });

    it('renders the stagnant ATTENTION state', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot({
        queueDepth: 12,
        activeJobs: 0,
        isStagnant: true,
        stagnantSince: '2026-08-17T11:00:00Z',
        stagnantThresholdMinutes: 20,
      }));
      const el: HTMLElement = fixture.nativeElement;
      const summary = el.querySelector('[data-testid="auto-review-queue-summary"]') as HTMLElement;

      expect(summary.classList.contains('rh__review--attention')).toBe(true);
      const attention = el.querySelector('[data-testid="auto-review-queue-attention"]');
      expect(attention?.textContent).toContain('20 minutes');
      fixture.destroy();
    });

    it('omits the summary section when no snapshot has loaded yet', async () => {
      const fixture = await mountWithReviewSnapshot(null);
      const el: HTMLElement = fixture.nativeElement;

      expect(el.querySelector('[data-testid="auto-review-queue-summary"]')).toBeFalsy();
      fixture.destroy();
    });
  });
});
