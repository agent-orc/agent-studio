import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { DeployEnvironmentCardComponent } from './deploy-environment-card';
import { buildEnvironmentCards, type EnvironmentCardView } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deploySourcesFixture,
  hostOnlySourcesFixture,
} from '../../testing/deploy-fixtures';

describe('DeployEnvironmentCardComponent', () => {
  let fixture: ComponentFixture<DeployEnvironmentCardComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const text = (id: string) => host().querySelector(`[data-testid="${id}"]`)?.textContent?.replace(/\s+/g, ' ').trim();
  const button = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLButtonElement | null;
  const fullCards = () => buildEnvironmentCards(deploySourcesFixture());
  const hostCards = () => buildEnvironmentCards(hostOnlySourcesFixture());

  async function render(card: EnvironmentCardView): Promise<void> {
    fixture.componentRef.setInput('environment', card);
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function emitted(): { runs: string[]; tools: number } {
    const result = { runs: [] as string[], tools: 0 };
    fixture.componentInstance.openRun.subscribe(id => result.runs.push(id));
    fixture.componentInstance.openTools.subscribe(() => result.tools++);
    return result;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeployEnvironmentCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DeployEnvironmentCardComponent);
  });

  it('shows release, age, cards, health, and the last AGT-3201 run', async () => {
    await render(fullCards()[0]);

    expect(text('deploy-environment-release')).toBe('Release 2026.10.19.2');
    expect(text('deploy-environment-age')).toBe('12 min ago');
    expect(text('deploy-environment-cards')).toBe('WEB-35');
    expect(text('deploy-environment-health')).toBe('Healthy');
    expect(text('deploy-environment-last-run')).toBe('Redeploy cancelled 5 min ago');
    expect(host().querySelector('[data-testid="deploy-environment-age"] time')?.getAttribute('datetime')).toBe('2026-10-19T11:48:00Z');
  });

  it('shows the newer release and an approval requested through AGT-3201, and opens that run for review', async () => {
    await render(fullCards()[1]);
    const events = emitted();

    expect(text('deploy-environment-newer')).toBe('Release 2026.10.19.2 is ready with 3 cards');
    expect(text('deploy-environment-last-run')).toBe('Deploy awaiting approval 11 min ago');
    expect(text('deploy-environment-approval')).toBe('Awaiting approval: Release 2026.10.19.2, requested by local.dev');
    button('deploy-environment-review-approval')!.click();
    expect(events).toEqual({ runs: ['run-c-production'], tools: 0 });
  });

  it('opens the last run by its deployment id', async () => {
    await render(fullCards()[0]);
    const events = emitted();

    button('deploy-environment-open-run')!.click();
    expect(events.runs).toEqual(['run-c-stage-redeploy']);
  });

  it('shows a guided failure decision and its actor on the environment card', async () => {
    await render({ ...fullCards()[0], intervention: {
      deploymentId: 'failed-run', failedStep: 'health', expiresAtUtc: null,
      decision: 'skip', decidedBy: 'local.owner',
    } });
    expect(text('deploy-environment-intervention')).toBe('health: skip by local.owner. Open run');
  });

  it('opens an older pending intervention while the last run is newer', async () => {
    await render({ ...fullCards()[0], intervention: {
      deploymentId: 'older-run', failedStep: 'health', expiresAtUtc: '2026-10-19T13:00:00Z',
      decision: null, decidedBy: null,
    } });
    const events = emitted();

    expect(text('deploy-environment-last-run')).toBe('Redeploy cancelled 5 min ago');
    expect(text('deploy-environment-intervention')).toContain('Waiting for intervention');
    button('deploy-environment-intervention')!.click();
    expect(events.runs).toEqual(['older-run']);
  });

  it('without the deploy service: names the artifact, reads host health, and routes a promotion to the tools', async () => {
    await render(hostCards()[1]);
    const events = emitted();

    expect(text('deploy-environment-release')).toBe('website@sha256:aaaaaaaaaaaa');
    expect(text('deploy-environment-health')).toBe('Healthy');
    expect(text('deploy-environment-cards')).toBe('Card lists arrive with release records');
    expect(text('deploy-environment-last-run')).toBe('Deploy succeeded 13 h ago');
    expect(text('deploy-environment-approval')).toBe('Awaiting approval: website@sha256:cccccccccccc');
    // A host report is not a deploy run, so there is nothing to open.
    expect(button('deploy-environment-open-run')).toBeNull();
    button('deploy-environment-review-approval')!.click();
    expect(events).toEqual({ runs: [], tools: 1 });
  });

  it('reads "No checks" only when the environment declares no health URL', async () => {
    const sources = hostOnlySourcesFixture();
    sources.environments!.environments[0].health = 'unknown';
    sources.environments!.environments[0].declaration.healthUrl = null;
    await render(buildEnvironmentCards(sources)[0]);
    expect(text('deploy-environment-health')).toBe('No checks');

    const unhealthy = deploySourcesFixture();
    unhealthy.statuses![0].health = 'unhealthy';
    await render(buildEnvironmentCards(unhealthy)[0]);
    expect(text('deploy-environment-health')).toBe('Unhealthy');
  });

  it('renders without a deployment and offers no run action', async () => {
    const environments = deployEnvironmentsFixture();
    environments.environments[1] = { ...environments.environments[1], deployedArtifact: null, history: [] };
    environments.promotions = [];
    await render(buildEnvironmentCards({ environments, statuses: null, deployments: [], releases: [] })[1]);

    expect(text('deploy-environment-release')).toBe('Nothing deployed yet');
    expect(text('deploy-environment-age')).toBe('Never');
    expect(text('deploy-environment-last-run')).toBe('No runs yet');
    expect(host().querySelectorAll('button')).toHaveLength(0);
  });

  describe('accessibility', () => {
    it('labels the card by its environment heading', async () => {
      await render(fullCards()[0]);
      const article = host().querySelector('article') as HTMLElement;
      const heading = host().querySelector(`#${article.getAttribute('aria-labelledby')}`) as HTMLElement;

      expect(heading.tagName).toBe('H3');
      expect(heading.textContent?.trim()).toBe('Staging');
    });

    it('names each action with its environment and keeps them in reading order', async () => {
      await render(fullCards()[1]);
      const buttons = Array.from(host().querySelectorAll('button'));

      expect(buttons.map(item => item.getAttribute('aria-label'))).toEqual([
        'Open last run for Production',
        'Review the pending approval for Production',
      ]);
      expect(buttons.every(item => item.type === 'button' && item.tabIndex === 0)).toBe(true);
    });

    it('states facts as a description list and announces health in words, not by colour alone', async () => {
      await render(fullCards()[0]);
      const terms = Array.from(host().querySelectorAll('dt')).map(dt => dt.textContent?.trim());

      expect(terms).toEqual(['Release', 'Deployed', 'Cards', 'Last run']);
      expect(host().querySelector('.env-card__health')?.textContent?.trim()).toBe('Health: Healthy');
    });
  });
});
