import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployHistoryComponent } from './project-deploy-history';
import { buildDeployRuns, type DeployPanel, type DeployRun } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deployEnvironmentsFixture, deployReleasesFixture } from '../../testing/deploy-fixtures';

const ENVIRONMENTS = [{ id: 'stage', name: 'Staging' }, { id: 'production', name: 'Production' }];

describe('ProjectDeployHistoryComponent', () => {
  let fixture: ComponentFixture<ProjectDeployHistoryComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => host().querySelector(`[data-testid="${id}"]`) as T | null;
  const rowIds = () => Array.from(host().querySelectorAll('tbody tr')).map(row => row.getAttribute('data-testid')!.replace('deploy-run-row-', ''));

  async function render(panel: DeployPanel<unknown>, runs: DeployRun[] = []): Promise<void> {
    fixture.componentRef.setInput('panel', panel);
    fixture.componentRef.setInput('runs', runs);
    fixture.componentRef.setInput('environments', ENVIRONMENTS);
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function choose(filter: 'environment' | 'outcome' | 'period', value: string): Promise<void> {
    const select = byTestId<HTMLSelectElement>(`deploy-history-filter-${filter}`)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const fixtureRuns = () => buildDeployRuns(deployEnvironmentsFixture(), deployReleasesFixture());

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployHistoryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployHistoryComponent);
  });

  it('shows loading, error, and empty states', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();

    await render({ status: 'error', message: 'Environments could not be loaded.' });
    expect(byTestId('deploy-history-error')).not.toBeNull();

    await render({ status: 'ready', value: {} }, []);
    expect(byTestId('deploy-history-empty')?.textContent).toContain('No deploy runs yet');
    expect(byTestId('deploy-history-filter-environment')).toBeNull();
  });

  it('lists every run newest first with release, outcome, and duration', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());

    expect(rowIds()).toEqual(['job-b-stage', 'job-a-stage-rollback', 'job-c-stage', 'job-a-production', 'job-a-stage']);
    const failed = byTestId('deploy-run-row-job-c-stage')!.textContent!.replace(/\s+/g, ' ');
    expect(failed).toContain('Staging');
    expect(failed).toContain('website@sha256:cccccccccccc');
    expect(failed).toContain('Failed');
    expect(failed).toContain('2 min 0 s');
    expect(byTestId('deploy-history-count')?.textContent?.trim()).toBe('5 of 5 runs');
  });

  it('filters by environment, outcome, and period, and clears all filters', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());

    await choose('environment', 'production');
    expect(rowIds()).toEqual(['job-a-production']);

    await choose('environment', 'all');
    await choose('outcome', 'failed');
    expect(rowIds()).toEqual(['job-c-stage']);

    await choose('outcome', 'all');
    await choose('period', '24h');
    expect(rowIds()).toEqual(['job-b-stage', 'job-a-stage-rollback', 'job-c-stage', 'job-a-production']);
    expect(byTestId('deploy-history-count')?.textContent?.trim()).toBe('4 of 5 runs');

    byTestId('deploy-history-reset')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(rowIds()).toHaveLength(5);
    expect(byTestId('deploy-history-reset')).toBeNull();
  });

  it('says when no run matches the filters', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());
    await choose('environment', 'production');
    await choose('outcome', 'failed');

    expect(byTestId('deploy-history-no-match')).not.toBeNull();
    expect(host().querySelector('table')).toBeNull();
  });

  it('opens a run and marks the selected row', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());
    const opened: string[] = [];
    fixture.componentInstance.openRun.subscribe(id => opened.push(id));

    byTestId('deploy-run-open-job-a-stage-rollback')!.click();
    fixture.componentRef.setInput('selectedRunId', 'job-a-stage-rollback');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(opened).toEqual(['job-a-stage-rollback']);
    expect(byTestId('deploy-run-row-job-a-stage-rollback')?.getAttribute('aria-current')).toBe('true');
    expect(byTestId('deploy-run-open-job-a-stage-rollback')?.getAttribute('aria-label')).toContain('Open rollback run on Staging');
  });

  it('labels every filter control', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());
    const labels = Array.from(host().querySelectorAll('.history__filters label'))
      .map(label => label.firstChild?.textContent?.trim());

    expect(labels).toEqual(['Environment', 'Outcome', 'Period']);
  });
});
