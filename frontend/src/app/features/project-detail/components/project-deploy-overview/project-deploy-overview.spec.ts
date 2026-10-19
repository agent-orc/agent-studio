import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployOverviewComponent } from './project-deploy-overview';
import { buildDeployRuns, buildEnvironmentCards, type DeployPanel, type EnvironmentCardView } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deployEnvironmentsFixture, deployReleasesFixture } from '../../testing/deploy-fixtures';

describe('ProjectDeployOverviewComponent', () => {
  let fixture: ComponentFixture<ProjectDeployOverviewComponent>;
  const host = () => fixture.nativeElement as HTMLElement;

  async function render(panel: DeployPanel<unknown>, cards: EnvironmentCardView[] = []): Promise<void> {
    fixture.componentRef.setInput('panel', panel);
    fixture.componentRef.setInput('cards', cards);
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function fixtureCards(): EnvironmentCardView[] {
    const data = deployEnvironmentsFixture();
    const releases = deployReleasesFixture();
    return buildEnvironmentCards(data, buildDeployRuns(data, releases), releases, null);
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployOverviewComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployOverviewComponent);
  });

  it('shows the loading surface while environments load', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
    expect(host().querySelector('app-deploy-environment-card')).toBeNull();
  });

  it('explains a failed environment read', async () => {
    await render({ status: 'error', message: 'Environments could not be loaded.' });
    expect(host().querySelector('[data-testid="deploy-environments-error"]')?.textContent).toContain('Environments could not be loaded');
  });

  it('invites setup when no environment is declared', async () => {
    await render({ status: 'ready', value: {} }, []);
    expect(host().querySelector('[data-testid="deploy-environments-empty"]')?.textContent).toContain('docs/deployments/environments/');
  });

  it('renders one status card per environment in Staging, Production order', async () => {
    await render({ status: 'ready', value: {} }, fixtureCards());

    const cards = Array.from(host().querySelectorAll('article')).map(article => article.getAttribute('data-testid'));
    expect(cards).toEqual(['deploy-environment-stage', 'deploy-environment-production']);
  });

  it('keeps the keyboard focus order Staging actions before Production actions', async () => {
    await render({ status: 'ready', value: {} }, fixtureCards());

    const order = Array.from(host().querySelectorAll('button')).map(button => button.getAttribute('aria-label'));
    expect(order).toEqual([
      'Open last run for Staging',
      'Open last run for Production',
      'Review the pending approval for Production',
    ]);
  });

  it('forwards run and tools requests from the cards', async () => {
    await render({ status: 'ready', value: {} }, fixtureCards());
    const runs: string[] = [];
    let tools = 0;
    fixture.componentInstance.openRun.subscribe(id => runs.push(id));
    fixture.componentInstance.openTools.subscribe(() => tools++);

    (host().querySelector('[aria-label="Open last run for Production"]') as HTMLButtonElement).click();
    (host().querySelector('[data-testid="deploy-environment-review-approval"]') as HTMLButtonElement).click();
    expect(runs).toEqual(['job-a-production']);
    expect(tools).toBe(1);
  });
});
