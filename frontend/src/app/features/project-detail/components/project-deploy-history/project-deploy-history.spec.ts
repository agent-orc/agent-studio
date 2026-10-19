import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployHistoryComponent } from './project-deploy-history';
import type { DeployPanel, DeployRun } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deployRunsFixture } from '../../testing/deploy-fixtures';

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

  const fixtureRuns = () => deployRunsFixture();

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployHistoryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployHistoryComponent);
  });

  it('shows loading, error, unavailable, and empty states', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();

    await render({ status: 'error', message: "Unknown project 'Website'" });
    expect(byTestId('deploy-history-error')?.textContent).toContain("Unknown project 'Website'");

    await render({ status: 'unavailable' });
    expect(byTestId('deploy-history-unavailable')?.textContent).toContain('once the deploy service records them');

    await render({ status: 'ready', value: {} }, []);
    expect(byTestId('deploy-history-empty')?.textContent).toContain('No deploy runs yet');
    expect(byTestId('deploy-history-filter-environment')).toBeNull();
  });

  it('lists every run newest first with trigger, release, outcome, requester, and duration', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());

    expect(rowIds()).toEqual([
      'run-c-stage-redeploy', 'run-c-production', 'run-c-stage', 'run-a-stage-rollback', 'run-b-stage', 'run-a-production', 'run-a-stage',
    ]);
    const cells = (id: string) => Array.from(byTestId(`deploy-run-row-${id}`)!.querySelectorAll('td')).map(cell => cell.textContent?.trim());
    // The Queued column (index 5) renders in the browser time zone.
    const withoutTime = (id: string) => cells(id).filter((_, index) => index !== 5);
    expect(withoutTime('run-b-stage')).toEqual(['Deploy', 'Staging', '2026.10.19.1', 'Failed', 'release-gate', '1 min 0 s']);
    expect(withoutTime('run-c-production')).toEqual(['Deploy', 'Production', '2026.10.19.2', 'Awaiting approval', 'local.dev', 'Not finished']);
    expect(cells('run-c-stage-redeploy').slice(0, 4)).toEqual(['Redeploy', 'Staging', '2026.10.19.2', 'Cancelled']);
    expect(cells('run-a-stage-rollback')[0]).toBe('Automatic rollback');
    expect(byTestId('deploy-history-count')?.textContent?.trim()).toBe('7 of 7 runs');
  });

  it('offers every deployment state as an outcome filter', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());
    const options = Array.from(byTestId<HTMLSelectElement>('deploy-history-filter-outcome')!.options).map(option => option.value);

    expect(options).toEqual(['all', 'queued', 'running', 'succeeded', 'failed', 'cancelled', 'awaiting-approval']);
  });

  it('filters by environment, outcome, and period, and clears all filters', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());

    await choose('environment', 'production');
    expect(rowIds()).toEqual(['run-c-production', 'run-a-production']);

    await choose('environment', 'all');
    await choose('outcome', 'cancelled');
    expect(rowIds()).toEqual(['run-c-stage-redeploy']);

    await choose('outcome', 'awaiting-approval');
    expect(rowIds()).toEqual(['run-c-production']);

    await choose('outcome', 'all');
    await choose('period', '24h');
    expect(rowIds()).toHaveLength(6);
    expect(byTestId('deploy-history-count')?.textContent?.trim()).toBe('6 of 7 runs');

    byTestId('deploy-history-reset')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(rowIds()).toHaveLength(7);
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

    byTestId('deploy-run-open-run-a-stage-rollback')!.click();
    fixture.componentRef.setInput('selectedRunId', 'run-a-stage-rollback');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(opened).toEqual(['run-a-stage-rollback']);
    expect(byTestId('deploy-run-row-run-a-stage-rollback')?.getAttribute('aria-current')).toBe('true');
    expect(byTestId('deploy-run-open-run-a-stage-rollback')?.getAttribute('aria-label')).toContain('Open Automatic rollback run on Staging');
  });

  it('labels every filter control', async () => {
    await render({ status: 'ready', value: {} }, fixtureRuns());
    const labels = Array.from(host().querySelectorAll('.history__filters label'))
      .map(label => label.firstChild?.textContent?.trim());

    expect(labels).toEqual(['Environment', 'Outcome', 'Period']);
  });
});
