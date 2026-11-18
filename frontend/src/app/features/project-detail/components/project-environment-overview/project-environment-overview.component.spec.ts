import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { ProjectEnvironmentDeclaration, ProjectEnvironments } from '../../../../models/project-overview.model';
import type { EnvironmentHealthResponse } from '../../../environment-health/models/environment-health.model';
import { DEPLOY_CLOCK } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  RELEASE_1,
  deployEnvironmentsFixture,
  deployStatusFixture,
  productionDownHealthFixture,
} from '../../testing/deploy-fixtures';
import { ProjectEnvironmentOverviewComponent } from './project-environment-overview.component';

async function mount(project = 'Alpha') {
  await TestBed.configureTestingModule({
    imports: [ProjectEnvironmentOverviewComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: DEPLOY_CLOCK, useValue: () => DEPLOY_FIXTURE_NOW }],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectEnvironmentOverviewComponent);
  fixture.componentRef.setInput('projectName', project);
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController) };
}

/** Answers the reads of the shared environment state projection; a missing deploy route answers 404. */
async function answer(fixture: Awaited<ReturnType<typeof mount>>['fixture'], http: HttpTestingController, project: string,
  sources: { environments: ProjectEnvironments; status?: unknown; health?: EnvironmentHealthResponse }): Promise<HTMLElement> {
  http.expectOne(`/api/projects/${project}/environments`).flush(sources.environments);
  const status = http.expectOne(`/api/projects/${project}/deploy/status`);
  if (sources.status === undefined) status.flush('', { status: 404, statusText: 'Not Found' });
  else status.flush(sources.status);
  http.expectOne(`/api/projects/${project}/environments/health`)
    .flush(sources.health ?? { capturedAt: '', probeOrigin: 'test', environments: [] });
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function declaration(id: string, name: string, kind: ProjectEnvironmentDeclaration['kind'] = 'stage'): ProjectEnvironmentDeclaration {
  return {
    id, name, kind, targetHostId: 'host', address: 'stage.example.test', template: 'container',
    destination: '/srv/app', containerPort: 8080, healthUrl: 'https://stage.example.test/health',
    approval: 'person', secretRefs: [],
  };
}

const text = (host: HTMLElement, selector: string) => host.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
const tone = (host: HTMLElement, selector: string) => host.querySelector<HTMLElement>(selector)?.dataset['tone'];

describe('ProjectEnvironmentOverviewComponent', () => {
  it('invites environment setup when no environment is declared', async () => {
    const { fixture, http } = await mount();
    const host = await answer(fixture, http, 'Alpha', { environments: { project: 'Alpha', environments: [], promotions: [], failureCards: [] } });
    expect(host.querySelector('[data-testid="project-environments-empty"]')).toBeTruthy();
    expect(host.textContent).toContain('Set up environments in Deploy');
  });

  it('shows the recorded health of each environment and runbooks only for stage and production', async () => {
    const { fixture, http } = await mount();
    const host = await answer(fixture, http, 'Alpha', { environments: {
      project: 'Alpha',
      environments: [
        { declaration: declaration('stage', 'Stage'), deployedArtifact: 'sha-abc', health: 'healthy', pendingArtifact: null, history: [] },
        { declaration: declaration('preview', 'Preview', 'other'), deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [] },
      ],
      promotions: [],
      failureCards: [],
    } });
    expect(text(host, '[data-testid="project-environment-stage"] [data-testid="environment-state-health"]')).toBe('Healthy');
    expect(tone(host, '[data-testid="project-environment-stage"] [data-testid="environment-state-health"]')).toBe('success');
    expect(text(host, '[data-testid="project-environment-stage"] [data-testid="environment-state-digest"]')).toBe('sha-abc');
    expect(text(host, '[data-testid="project-environment-preview"] [data-testid="environment-state-health"]')).toBe('No data');
    expect(tone(host, '[data-testid="project-environment-preview"] [data-testid="environment-state-health"]')).toBe('neutral');
    expect(text(host, '[data-testid="project-environment-preview"] [data-testid="environment-state-release"]')).toBe('Nothing deployed yet');
    expect(host.querySelector('[data-testid="project-environment-stage"] [data-testid="environment-runbooks"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-environment-preview"] [data-testid="environment-runbooks"]')).toBeNull();
  });

  it('M2: reads Production as down from the open incident while the last deploy report says healthy', async () => {
    const { fixture, http } = await mount('Website');
    const host = await answer(fixture, http, 'Website', {
      environments: deployEnvironmentsFixture(), status: deployStatusFixture(), health: productionDownHealthFixture(),
    });
    const production = '[data-testid="project-environment-production"]';
    expect(text(host, `${production} [data-testid="environment-state-health"]`)).toBe('Down');
    expect(tone(host, `${production} [data-testid="environment-state-health"]`)).toBe('danger');
    expect(text(host, '[data-testid="project-environment-stage"] [data-testid="environment-state-health"]')).toBe('Up');
    // The release version and its age lead; the digest is secondary detail.
    expect(text(host, `${production} [data-testid="environment-state-release"]`)).toBe(`Release ${RELEASE_1.version} deployed 13 h ago`);
    expect(text(host, `${production} [data-testid="environment-state-digest"]`)).toBe('website@sha256:aaaaaaaaaaaa');
    expect(text(host, production)).not.toContain('registry.example/website@sha256');
  });

  it('shows uncertainty after a failed host report instead of naming its target', async () => {
    const { fixture, http } = await mount();
    const artifact = 'app@sha256:' + 'b'.repeat(64);
    const host = await answer(fixture, http, 'Alpha', {
      environments: { project: 'Alpha', environments: [
        { declaration: declaration('stage', 'Stage'), deployedArtifact: artifact, health: 'failed', pendingArtifact: null,
          history: [{ at: '2026-11-16T12:00:00Z', status: 'failed', artifact, previousArtifact: null, evidence: 'health-http-failed' }] },
      ], promotions: [], failureCards: [] },
      status: [{
        environment: { id: 'stage' }, currentRelease: { version: '2026.11.16.1', artifactDigests: [artifact] },
        currentDeployment: null, newerRelease: null, cardsSinceLastDeploy: [], health: 'unhealthy', releaseUncertain: true,
      }],
    });
    expect(text(host, '[data-testid="project-environment-stage"] [data-testid="environment-state-release"]'))
      .toBe('Running release uncertain. A failed release may be running.');
    expect(host.querySelector('[data-testid="project-environment-stage"] [data-testid="environment-state-digest"]')).toBeNull();
  });
});
