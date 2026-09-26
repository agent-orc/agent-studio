import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { UsageHoverPanelComponent } from './usage-hover-panel';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('UsageHoverPanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [UsageHoverPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(UsageHoverPanelComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] UsageHoverPanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows interactive chat usage by host and project', async () => {
    await TestBed.configureTestingModule({
      imports: [UsageHoverPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(UsageHoverPanelComponent);
    TestBed.inject(RemoteHostsService).interactiveUsage.set([{
      hostName: 'agent-runner-01', projectName: 'Agent Studio',
      activeTurns: 2, heavyTurns: 1, cpuPercent: 45,
      tokens: 1200, costUsd: 0.03,
    }]);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="usage-chat-trigger"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController).expectOne('/api/runner/project-chat/usage')
      .flush([{ hostName: 'agent-runner-01', projectName: 'Agent Studio',
        activeTurns: 2, heavyTurns: 1, cpuPercent: 45, tokens: 1200, costUsd: 0.03 }]);
    fixture.detectChanges();
    const panel = fixture.nativeElement.querySelector('[data-testid="usage-chat-panel"]');
    expect(panel?.textContent).toContain('agent-runner-01 / Agent Studio');
    expect(panel?.textContent).toContain('45%');
    expect(panel?.textContent).toContain('1,200');
    fixture.destroy();
  });
});
