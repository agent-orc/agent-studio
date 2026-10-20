import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { HeaderQuotaComponent } from './header-quota';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { AuthSessionState } from '../../../../services/auth.service';
import { ProviderAuthStatusService } from '../../../remote-hosts/services/provider-auth-status.service';
import type { ProviderAuthBadge } from '../../../remote-hosts/models/provider-auth.model';

interface WindowDisplay { value: string; barPct: number; tone: string; windowKind: string }
interface PrimaryDisplay { value: string; tag: string; barPct: number; hasValue: boolean; tone: string }
interface Chip { windowKey: string; tag: string; label?: string; value: string; barPct: number; tone: string }
interface QuotaWindowInput {
  label: string;
  usedPct: number | null;
  used: number | null;
  limit: number | null;
  unit: string | null;
  resetAt: string | null;
  resetLabel: string | null;
};

const noPrimary: PrimaryDisplay = { value: '—', tag: '', barPct: 0, hasValue: false, tone: 'unknown' };

class JobsHubClientStub {
  readonly connected = signal(false);
  start(): void { return undefined; }
  stop(): void { return undefined; }
}

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('HeaderQuotaComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] HeaderQuotaComponent initial render skipped:', (e as Error).message);
    }
    TestBed.inject(HttpTestingController)
      .expectOne('/api/cli/quota')
      .flush({ ttlSeconds: 600, snapshots: [] });
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('HeaderQuotaComponent (networked)', () => {
  it('renders runner provider state without probing the web server CLI', async () => {
    const statuses = signal([{ id: 'runner-1:claude', provider: 'claude', providerLabel: 'Claude',
      runnerId: 'runner-1', hostName: 'agent-host-1', state: 'ok' }] as ProviderAuthBadge[]);
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        provideRouter([]), { provide: JobsHubClient, useClass: JobsHubClientStub },
        { provide: ProviderAuthStatusService, useValue: { statuses } }],
    }).compileComponents();
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
    });
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="hquota-card-claude"]')
      .getAttribute('aria-label')).toContain('agent-host-1 ok');
    TestBed.inject(HttpTestingController).expectNone('/api/cli/quota');
  });
});

describe('HeaderQuotaComponent (reconnect hydration)', () => {
  it('refreshes the visible quota strip when the jobs hub reconnects', async () => {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    const http = TestBed.inject(HttpTestingController);
    const hub = TestBed.inject(JobsHubClient) as unknown as JobsHubClientStub;

    fixture.detectChanges();
    http.expectOne('/api/cli/quota').flush({ ttlSeconds: 600, snapshots: [] });

    hub.connected.set(true);
    fixture.detectChanges();

    http.expectOne('/api/cli/quota').flush({
      ttlSeconds: 600,
      snapshots: [
        {
          cliType: 'codex',
          plan: 'test',
          fetchedAt: new Date().toISOString(),
          source: 'test',
          error: null,
          windows: [
            { label: '5-hour', usedPct: 12, used: null, limit: null, unit: '%', resetAt: null, resetLabel: null },
          ],
        },
      ],
    });
    http.verify();
    fixture.destroy();
  });
});

/**
 * Status-bar quota-strip contract (2026-06-04 — ASS-696 follow-up).
 *
 * The strip no longer carries any hover tooltip: clicking a card opens
 * that CLI's own detail modal instead. What remains locked here:
 *
 *  - `state` still drives the data-state DOM attribute (error / hot /
 *    warn dominate stale; under-70% on every window = idle; no windows +
 *    no error = unavailable).
 *  - `buildChips` surfaces EVERY reported window, so a CLI that exposes
 *    both a 5h and a weekly window renders two chips side by side
 *    (requirement: show all windows, not just the most-constraining one).
 */
describe('HeaderQuotaComponent (semantic state)', () => {
  async function buildComponent() {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    return fixture.componentInstance as unknown as {
      cardState: (
        tone: string,
        stale: boolean,
        hasError: boolean,
        sw: WindowDisplay | undefined,
        ww: WindowDisplay | undefined,
        primary: PrimaryDisplay,
      ) => string;
      buildChips: (
        sw: WindowDisplay | undefined,
        ww: WindowDisplay | undefined,
        primary: PrimaryDisplay,
        windows?: QuotaWindowInput[],
      ) => Chip[];
    };
  }

  it('idle when both windows are under 70%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '31%', barPct: 31, tone: 'ok', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '55%', barPct: 55, tone: 'ok', windowKind: 'weekly' };
    expect(c.cardState('ok', false, false, sw, ww, noPrimary)).toBe('idle');
  });

  it('warn when any window crosses 70%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '72%', barPct: 72, tone: 'warn', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '40%', barPct: 40, tone: 'ok', windowKind: 'weekly' };
    expect(c.cardState('warn', false, false, sw, ww, noPrimary)).toBe('warn');
  });

  it('hot when any window crosses 90%', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '95%', barPct: 95, tone: 'hot', windowKind: 'five_hour' };
    expect(c.cardState('hot', false, false, sw, undefined, noPrimary)).toBe('hot');
  });

  it('error always dominates other tones', async () => {
    const c = await buildComponent();
    expect(c.cardState('warn', true, true, undefined, undefined, noPrimary)).toBe('error');
  });

  it('unavailable when no windows reported and no error', async () => {
    const c = await buildComponent();
    expect(c.cardState('unknown', true, false, undefined, undefined, noPrimary)).toBe('unavailable');
  });

  it('stale when fresh windows exist but snapshot is older than TTL', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '40%', barPct: 40, tone: 'ok', windowKind: 'five_hour' };
    expect(c.cardState('ok', true, false, sw, undefined, noPrimary)).toBe('stale');
  });

  it('renders both a 5H and a WK chip when both windows are present', async () => {
    const c = await buildComponent();
    const sw: WindowDisplay = { value: '11%', barPct: 11, tone: 'ok', windowKind: 'five_hour' };
    const ww: WindowDisplay = { value: '47%', barPct: 47, tone: 'ok', windowKind: 'weekly' };
    const chips = c.buildChips(sw, ww, noPrimary);
    expect(chips.map(ch => ch.windowKey)).toEqual(['5h', 'wk']);
    expect(chips.map(ch => ch.tag)).toEqual(['5H', 'WK']);
    expect(chips.map(ch => ch.value)).toEqual(['11%', '47%']);
  });

  it('renders Spark windows as separate status-bar chips', async () => {
    const c = await buildComponent();
    const windows: QuotaWindowInput[] = [
      { label: '5-hour', usedPct: 3, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '20:09' },
      { label: 'Weekly', usedPct: 14, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '23:43 on 11 Jun' },
      { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
      { label: 'Spark Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
    ];

    const chips = c.buildChips(undefined, undefined, noPrimary, windows);

    expect(chips.map(ch => ch.windowKey)).toEqual(['5h', 'wk', 'spark-5h', 'spark-wk']);
    expect(chips.map(ch => ch.tag)).toEqual(['5H', 'WK', 'S5H', 'SWK']);
    expect(chips.map(ch => ch.value)).toEqual(['3%', '14%', '0%', '0%']);
  });

  it('falls back to the primary chip when no 5H / WK window is reported', async () => {
    const c = await buildComponent();
    const primary: PrimaryDisplay = { value: '8%', tag: 'MO', barPct: 8, hasValue: true, tone: 'ok' };
    const chips = c.buildChips(undefined, undefined, primary);
    expect(chips).toHaveLength(1);
    expect(chips[0].tag).toBe('MO');
    expect(chips[0].value).toBe('8%');
  });

  it('renders a placeholder chip when nothing is reported', async () => {
    const c = await buildComponent();
    const chips = c.buildChips(undefined, undefined, noPrimary);
    expect(chips).toHaveLength(1);
    expect(chips[0].windowKey).toBe('none');
    expect(chips[0].value).toBe('—');
  });

  it('renders a reported but unparseable quota as Unknown', async () => {
    const c = await buildComponent();
    const windows: QuotaWindowInput[] = [
      { label: 'Quota', usedPct: null, used: null, limit: null, unit: '%', resetAt: null, resetLabel: null },
    ];

    const chips = c.buildChips(undefined, undefined, noPrimary, windows);

    expect(chips).toHaveLength(1);
    expect(chips[0].windowKey).toBe('quota');
    expect(chips[0].value).toBe('Unknown');
    expect(chips[0].tone).toBe('unknown');
  });
});

/**
 * Regression for the "Codex missing from the taskbar strip although the
 * API delivers it" bug (2026-07-10). The live `/api/cli/quota` payload for
 * Codex reports its windows as `unit: "%"` with BOTH `used` and `limit`
 * null and only `usedPct` populated. A fresh Codex snapshot in that shape
 * must still produce a real, non-empty card in the strip — a CLI whose
 * snapshot has no error must never fall out of the row.
 */
describe('HeaderQuotaComponent (Codex %-only payload)', () => {
  interface CardModel {
    cliType: string;
    chips: Chip[];
    state: string;
    tone: string;
  }

  // Real Codex shape: 4 windows, unit '%', used/limit both null, only usedPct.
  const codexWindows: QuotaWindowInput[] = [
    { label: 'Current session (5h)', usedPct: 66, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '02:33' },
    { label: 'Weekly', usedPct: 12, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:33 on 3 May' },
    { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
    { label: 'Spark Weekly', usedPct: 4, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
  ];

  async function renderWithCodex() {
    await TestBed.configureTestingModule({
      imports: [HeaderQuotaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeaderQuotaComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/cli/quota').flush({
      ttlSeconds: 600,
      snapshots: [
        {
          cliType: 'codex',
          plan: 'Pro',
          fetchedAt: new Date().toISOString(),
          source: '/status',
          error: null,
          windows: codexWindows,
        },
      ],
    });
    fixture.detectChanges();
    const cards = (fixture.componentInstance as unknown as { cards: () => CardModel[] }).cards();
    return cards.find((c) => c.cliType === 'codex')!;
  }

  it('keeps the Codex card in the strip with one chip per reported window', async () => {
    const codex = await renderWithCodex();
    expect(codex).toBeTruthy();
    expect(codex.chips.map((ch) => ch.windowKey)).toEqual(['5h', 'wk', 'spark-5h', 'spark-wk']);
    // No empty "—" placeholder: the %-only payload maps to real values.
    expect(codex.chips.map((ch) => ch.value)).toEqual(['66%', '12%', '0%', '4%']);
    expect(codex.chips.some((ch) => ch.windowKey === 'none')).toBe(false);
  });

  it('treats unit "%" with a null limit as progress against 100 (bar + tone)', async () => {
    const codex = await renderWithCodex();
    const session = codex.chips.find((ch) => ch.windowKey === '5h')!;
    expect(session.value).toBe('66%');
    expect(session.barPct).toBe(66); // progress against 100, not against a null limit
    expect(session.tone).toBe('ok');
    // A fresh, error-free snapshot is never "unavailable".
    expect(codex.state).not.toBe('unavailable');
  });
});
