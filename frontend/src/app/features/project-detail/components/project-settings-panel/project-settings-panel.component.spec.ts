import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { By } from '@angular/platform-browser';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { ProjectSettingsPanelComponent } from './project-settings-panel.component';
import { ProjectBasicsCardComponent } from '../project-basics-card/project-basics-card.component';
import type { RegistryProjectSummary } from '../../../../models/task.model';
import { ServerCheckoutStore, serverCheckoutView } from '../../../server-checkout';
import { urlProject } from '../../../server-checkout/testing/server-checkout.fixtures';

const CHECKOUT_MISSING_GATE = {
  profile: null, gateApplicable: false, verifyPlan: { source: 'none', commands: [] },
  verifyDefinition: { state: 'checkout-missing', path: '.agent-studio/project.yml', message: null,
    exampleStack: null, exampleDefinition: null, examplePrepareScript: null },
};

/** Settings for one registered project with a missing checkout, under the local profile. */
async function settingsWithoutCheckout(project: RegistryProjectSummary) {
  const store = {
    load: vi.fn(), start: vi.fn(), statusFor: () => null,
    projectFor: (reference: { id?: string | null; name?: string | null }) =>
      project.id === reference.id || project.displayName === reference.name ? project : null,
    viewFor: (candidate: RegistryProjectSummary | null) => candidate ? serverCheckoutView(candidate, null) : null,
  };
  await TestBed.configureTestingModule({
    imports: [ProjectSettingsPanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([]), { provide: ServerCheckoutStore, useValue: store }],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectSettingsPanelComponent);
  fixture.componentRef.setInput('projectName', project.displayName);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne(`/api/projects/${project.displayName}/build-profile`).flush(CHECKOUT_MISSING_GATE);
  const workspaces = [{ id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
    createdAt: project.createdAt, projects: [project] }];
  for (const request of http.match(req => req.url === '/api/v1/workspaces')) request.flush(workspaces);
  for (const request of http.match(req => req.url.endsWith('/projects/settings'))) request.flush({});
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  return {
    checkoutNotices: host.querySelectorAll('[data-testid="server-checkout-notice"]'),
    verifyNotice: host.querySelector('[data-testid="project-settings-no-verify-commands"]'),
  };
}

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('ProjectSettingsPanelComponent (smoke)', () => {
  it('links review aspects to Deploy Run steps for this project', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSettingsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectSettingsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Library');
    fixture.componentInstance.openReviewAspects();
    expect(TestBed.inject(StudioTabStateService).activeTab())
      .toEqual({ kind: 'hub', projectName: 'Library', section: 'pipeline' });
  });

  it('compiles + instantiates without throwing', async () => {
    expect(ProjectSettingsPanelComponent).toBeTruthy();
  });

  it('refreshes the verify gate when Project basics saves paths', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSettingsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectSettingsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Library');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Library/build-profile').flush({ gateApplicable: true,
      verifyDefinition: { state: 'checkout-missing' } });
    const basics = fixture.debugElement.query(By.directive(ProjectBasicsCardComponent))
      ?.componentInstance as ProjectBasicsCardComponent;
    expect(basics).toBeTruthy();
    basics.pathsSaved.emit();
    http.expectOne('/api/projects/Library/build-profile').flush({ gateApplicable: true,
      verifyDefinition: { state: 'ready' } });
    expect(fixture.componentInstance.buildProfileGate()?.verifyDefinition?.state).toBe('ready');
  });

  it('offers only Create server checkout for a URL project without a checkout', async () => {
    const { checkoutNotices, verifyNotice } = await settingsWithoutCheckout(urlProject());
    expect(checkoutNotices).toHaveLength(1);
    expect(checkoutNotices[0].querySelector('[data-testid="server-checkout-create"]')?.textContent)
      .toContain('Create server checkout');
    expect(verifyNotice).toBeNull();
  });

  it('links the checkout path field for a local project whose checkout is missing', async () => {
    const { checkoutNotices, verifyNotice } = await settingsWithoutCheckout(urlProject({ repositoryUrl: null }));
    expect(checkoutNotices).toHaveLength(0);
    expect(verifyNotice?.textContent).toContain('Repository checkout missing');
    expect(verifyNotice?.querySelector('a')?.getAttribute('href')).toBe('#project-basics-repository-path');
  });
});
