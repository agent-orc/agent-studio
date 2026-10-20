import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployOverviewComponent } from './project-deploy-overview';
import { buildEnvironmentCards, type DeployPanel, type EnvironmentCardView } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deploySourcesFixture } from '../../testing/deploy-fixtures';

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

  const fixtureCards = (): EnvironmentCardView[] => buildEnvironmentCards(deploySourcesFixture());

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

  it('explains a failed environment read with the server message', async () => {
    await render({ status: 'error', message: "Unknown project 'Website'" });
    const error = host().querySelector('[data-testid="deploy-environments-error"]')?.textContent;
    expect(error).toContain('Environments could not be loaded');
    expect(error).toContain("Unknown project 'Website'");
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
    const headings = Array.from(host().querySelectorAll('article h3')).map(heading => heading.textContent?.trim());
    expect(headings).toEqual(['Staging', 'Production']);
  });

  it('forwards run requests from the cards', async () => {
    await render({ status: 'ready', value: {} }, fixtureCards());
    const runs: string[] = [];
    fixture.componentInstance.openRun.subscribe(id => runs.push(id));

    (host().querySelector('[aria-label="Open last run for Staging"]') as HTMLButtonElement).click();
    (host().querySelector('[data-testid="deploy-environment-review-approval"]') as HTMLButtonElement).click();
    expect(runs).toEqual(['run-c-stage-redeploy', 'run-c-production']);
  });
});
