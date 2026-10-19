import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { BehaviorSubject, type Observable } from 'rxjs';
import { ProjectDeployAreaComponent } from './project-deploy-area';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { DEPLOY_CLOCK, type DeployPanel } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deployLifecycleFixture,
  deployReleasesFixture,
} from '../../testing/deploy-fixtures';

class FakeDeployData {
  readonly environmentsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly releasesPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly healthPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly lifecyclePanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly requests: string[] = [];

  environments(project: string): Observable<DeployPanel<unknown>> { this.requests.push(project); return this.environmentsPanel; }
  releases(): Observable<DeployPanel<unknown>> { return this.releasesPanel; }
  healthSummaries(): Observable<DeployPanel<unknown>> { return this.healthPanel; }
  lifecycle(): Observable<DeployPanel<unknown>> { return this.lifecyclePanel; }
}

describe('ProjectDeployAreaComponent', () => {
  let fixture: ComponentFixture<ProjectDeployAreaComponent>;
  let data: FakeDeployData;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function click(id: string): Promise<void> {
    byTestId(id)!.click();
    await settle();
  }

  beforeEach(async () => {
    data = new FakeDeployData();
    await TestBed.configureTestingModule({
      imports: [ProjectDeployAreaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ProjectDeployDataService, useValue: data },
        { provide: DEPLOY_CLOCK, useValue: () => DEPLOY_FIXTURE_NOW },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployAreaComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    document.body.appendChild(host());
    await settle();
  });

  afterEach(() => host().remove());

  it('shows no preview or fixture data while sources load', () => {
    expect(host().textContent).not.toContain('Preview data');
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
    expect(host().querySelector('app-deploy-environment-card')).toBeNull();
  });

  it('works with partial data: environments ready, releases unavailable, health checks absent', async () => {
    data.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    data.releasesPanel.next({ status: 'unavailable' });
    await settle();

    expect(host().querySelectorAll('app-deploy-environment-card')).toHaveLength(2);
    expect(byTestId('deploy-environment-stage')?.textContent).toContain('website@sha256:bbbbbbbbbbbb');
    expect(byTestId('deploy-environment-stage')?.textContent).toContain('No checks');
    expect(byTestId('deploy-environment-stage')?.textContent).toContain('Card lists arrive with release records');

    await click('deploy-tab-releases');
    expect(byTestId('deploy-releases-unavailable')).not.toBeNull();

    await click('deploy-tab-pipeline');
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
  });

  it('names releases and their cards once release records exist', async () => {
    data.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    data.releasesPanel.next({ status: 'ready', value: deployReleasesFixture() });
    data.lifecyclePanel.next({ status: 'ready', value: deployLifecycleFixture() });
    await settle();

    expect(byTestId('deploy-environment-stage')?.textContent).toContain('Release 2026.10.19.1');
    expect(byTestId('deploy-environment-stage')?.textContent).toContain('WEB-33, WEB-34, WEB-35');
    await click('deploy-tab-pipeline');
    expect(byTestId('deploy-stage-production')).not.toBeNull();
  });

  it('keeps the other panels when the environment read fails', async () => {
    data.environmentsPanel.next({ status: 'error', message: 'Environments could not be loaded.' });
    data.releasesPanel.next({ status: 'ready', value: deployReleasesFixture() });
    await settle();

    expect(byTestId('deploy-environments-error')).not.toBeNull();
    await click('deploy-tab-releases');
    expect(byTestId('deploy-release-detail')).not.toBeNull();
    await click('deploy-tab-history');
    expect(byTestId('deploy-history-error')).not.toBeNull();
  });

  it('opens the run drawer from a status card and returns focus to the opener on close', async () => {
    data.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    await settle();
    const opener = host().querySelector('[aria-label="Open last run for Staging"]') as HTMLButtonElement;
    opener.focus();
    opener.click();
    await settle();

    expect(byTestId('deploy-run-drawer')?.textContent).toContain('Deploy to Staging');
    expect(host().querySelector('.deploy-area__body--drawer')).not.toBeNull();

    (host().querySelector('[data-testid="sidesheet-close"]') as HTMLButtonElement).click();
    await settle();
    await Promise.resolve();
    expect(byTestId('deploy-run-drawer')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('opens a run from the history table and marks its row', async () => {
    data.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    await settle();
    await click('deploy-tab-history');
    await click('deploy-run-open-job-c-stage');

    expect(byTestId('deploy-run-outcome')?.textContent?.trim()).toBe('Failed');
    expect(byTestId('deploy-run-row-job-c-stage')?.getAttribute('aria-current')).toBe('true');
  });

  it('reloads every source on Refresh and keeps the deployment tools reachable', async () => {
    await click('deploy-refresh');
    expect(data.requests).toEqual(['Website', 'Website']);

    await click('deploy-open-tools');
    expect(host().querySelector('app-project-deployment-panel')).not.toBeNull();
  });

  it('marks the active tab for assistive technology and keeps P2 tabs disabled', async () => {
    await click('deploy-tab-history');

    expect(byTestId('deploy-tab-history')?.getAttribute('aria-current')).toBe('page');
    expect(byTestId('deploy-tab-overview')?.getAttribute('aria-current')).toBeNull();
    const disabled = Array.from(host().querySelectorAll('.deploy-tabs button:disabled')).map(button => button.textContent?.trim());
    expect(disabled).toEqual(['Variables · P2', 'Runbooks · P2', 'Operations · P2']);
  });
});
