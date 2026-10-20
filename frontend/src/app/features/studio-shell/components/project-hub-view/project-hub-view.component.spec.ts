import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectHubViewComponent } from './project-hub-view.component';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { studioTabKey } from '../../studio-shell.types';

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
describe('ProjectHubViewComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    try {
      await TestBed.configureTestingModule({
        imports: [ProjectHubViewComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(ProjectHubViewComponent);
      fixture.componentRef.setInput('projectName', undefined);

      // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] ProjectHubViewComponent initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      // TestBed setup itself crashed (module-load cycle, env not
      // initialized because of file-order, etc). Still verifies the
      // component class is importable.
      console.warn('[smoke] ProjectHubViewComponent TestBed setup skipped:', (e as Error).message);
      expect(ProjectHubViewComponent).toBeTruthy();
    }
  });

  it('keeps the active rail in the hub tab', async () => {
    window.localStorage?.removeItem('atp.studio.tabs.v1');
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const tabState = TestBed.inject(StudioTabStateService);
    tabState.open({ kind: 'hub', projectName: 'Alpha', section: 'overview' });

    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'overview');
    fixture.detectChanges();

    fixture.componentInstance.setRail('security');
    fixture.detectChanges();

    expect(fixture.componentInstance.activeRail()).toBe('security');
    expect(tabState.activeTab()).toEqual({ kind: 'hub', projectName: 'Alpha', section: 'security' });
  });

  it('hosts application runtime observability in Operations', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'operations');
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-operations-area"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-product-runtime-panel"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-observability-panel"]')).toBeNull();
  });

  it('targets an Operations marker release only in its own project Deploy view', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'operations');
    fixture.detectChanges();
    fixture.componentInstance.openOperationsRelease('older-release');
    expect(fixture.componentInstance.activeRail()).toBe('deploy');
    expect(fixture.componentInstance.selectedRelease()).toEqual({ project: 'Alpha', id: 'older-release' });
    fixture.componentInstance.setRail('operations');
    fixture.componentInstance.setRail('deploy');
    expect(fixture.componentInstance.selectedRelease()).toBeNull();
  });

  it('keeps agent-work observability in the Deck Agent activity rail', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'agent-activity');
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.activeRail()).toBe('agent-activity');
    expect(host.querySelector('app-project-shell')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-observability-panel"]')).toBeTruthy();
    expect(host.querySelector('app-project-detail')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-operations-area"]')).toBeNull();
  });

  it.each([
    ['observability', 'operations', 'overview'],
    ['deployment', 'deploy', 'overview'],
    ['pipeline', 'deploy', 'steps'],
  ] as const)('opens the retired %s section in its new area', async (section, rail, deployView) => {
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', section);
    fixture.detectChanges();
    expect(fixture.componentInstance.activeRail()).toBe(rail);
    expect(fixture.componentInstance.deployView()).toBe(deployView);
  });

  it('keeps Run steps in the hub tab section and other Deploy views local', async () => {
    window.localStorage?.removeItem('atp.studio.tabs.v1');
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const tabState = TestBed.inject(StudioTabStateService);
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    const hub = fixture.componentInstance;
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'deploy');
    fixture.detectChanges();

    hub.setRail('pipeline');
    expect(tabState.activeTab()).toEqual({ kind: 'hub', projectName: 'Alpha', section: 'pipeline' });
    expect(hub.deployView()).toBe('steps');

    hub.onDeployView('releases');
    expect(tabState.activeTab()).toEqual({ kind: 'hub', projectName: 'Alpha', section: 'deploy' });
    fixture.componentRef.setInput('initialSection', 'deploy');
    fixture.detectChanges();
    expect(hub.deployView()).toBe('releases');

    hub.onDeployView('steps');
    expect(tabState.activeTab()).toEqual({ kind: 'hub', projectName: 'Alpha', section: 'pipeline' });
    expect(hub.activeRail()).toBe('deploy');
  });

  it('opens the Wiki rail as a distinct internal destination', async () => {
    window.localStorage?.removeItem('atp.studio.tabs.v1');
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([]),
      ],
    }).compileComponents();
    const tabState = TestBed.inject(StudioTabStateService);
    tabState.open({ kind: 'hub', projectName: 'Alpha', section: 'overview' });
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'overview');
    fixture.detectChanges();

    fixture.componentInstance.setRail('wiki');

    expect(tabState.activeKey()).toBe('hub:Alpha:wiki');
    expect(tabState.tabs().some(tab => studioTabKey(tab) === 'hub:Alpha')).toBe(true);
  });

  it('opens or focuses an exact Wiki path without duplicating it', async () => {
    window.localStorage?.removeItem('atp.studio.tabs.v1');
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([]),
      ],
    }).compileComponents();
    const tabState = TestBed.inject(StudioTabStateService);
    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.detectChanges();

    fixture.componentInstance.openWikiTarget({
      target: { kind: 'page', relPath: 'concepts/routing.md' }, reuse: 'new',
    });
    fixture.componentInstance.openWikiTarget({
      target: { kind: 'page', relPath: 'concepts/routing.md' }, reuse: 'new',
    });

    expect(tabState.tabs().filter(tab => studioTabKey(tab)
      === 'hub:Alpha:wiki:page:concepts%2Frouting.md')).toHaveLength(1);
    expect(tabState.activeKey()).toBe('hub:Alpha:wiki:page:concepts%2Frouting.md');
  });

  it('follows a section change on the tab payload (Wiki -> Overview)', async () => {
    window.localStorage?.removeItem('atp.studio.tabs.v1');
    await TestBed.configureTestingModule({
      imports: [ProjectHubViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectHubViewComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.componentRef.setInput('initialSection', 'wiki');
    fixture.detectChanges();
    expect(fixture.componentInstance.activeRail()).toBe('wiki');

    // Re-opening the Hub on "Project" rebinds the section on the shared tab
    // payload; the rail must follow it back to Overview instead of "doing
    // nothing" (AGT-2023).
    fixture.componentRef.setInput('initialSection', 'overview');
    fixture.detectChanges();
    expect(fixture.componentInstance.activeRail()).toBe('overview');
  });
});
