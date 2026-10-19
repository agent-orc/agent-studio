import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { DeployEnvironmentCardComponent } from './deploy-environment-card';
import { buildDeployRuns, buildEnvironmentCards, type EnvironmentCardView } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deployHealthFixture,
  deployReleasesFixture,
} from '../../testing/deploy-fixtures';

function fixtureCards(withReleases = true, withHealth = true): EnvironmentCardView[] {
  const data = deployEnvironmentsFixture();
  const releases = withReleases ? deployReleasesFixture() : null;
  return buildEnvironmentCards(data, buildDeployRuns(data, releases), releases, withHealth ? deployHealthFixture() : null);
}

describe('DeployEnvironmentCardComponent', () => {
  let fixture: ComponentFixture<DeployEnvironmentCardComponent>;
  const text = (id: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`)?.textContent?.replace(/\s+/g, ' ').trim();

  async function render(card: EnvironmentCardView): Promise<void> {
    fixture.componentRef.setInput('environment', card);
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeployEnvironmentCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DeployEnvironmentCardComponent);
  });

  it('shows release, age, cards, health, and the last run outcome', async () => {
    await render(fixtureCards()[0]);

    expect(text('deploy-environment-release')).toBe('Release 2026.10.19.1');
    expect(text('deploy-environment-age')).toBe('12 min ago');
    expect(text('deploy-environment-cards')).toBe('WEB-33, WEB-34, WEB-35');
    expect(text('deploy-environment-health')).toBe('Healthy');
    expect(text('deploy-environment-last-run')).toBe('Deploy succeeded 12 min ago');
    const time = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="deploy-environment-age"] time')!;
    expect(time.getAttribute('datetime')).toBe('2026-10-19T11:48:00Z');
  });

  it('reads "No checks" without health checks and names the artifact without a release record', async () => {
    await render(fixtureCards(false, false)[0]);

    expect(text('deploy-environment-health')).toBe('No checks');
    expect(text('deploy-environment-release')).toBe('website@sha256:bbbbbbbbbbbb');
    expect(text('deploy-environment-cards')).toBe('Card lists arrive with release records');
  });

  it('shows a pending approval and routes it to the deployment tools', async () => {
    await render(fixtureCards()[1]);
    const emitted: number[] = [];
    fixture.componentInstance.openTools.subscribe(() => emitted.push(1));

    expect(text('deploy-environment-approval')).toBe('Awaiting approval: website@sha256:bbbbbbbbbbbb');
    (fixture.nativeElement.querySelector('[data-testid="deploy-environment-review-approval"]') as HTMLButtonElement).click();
    expect(emitted).toEqual([1]);
  });

  it('opens the last run by id', async () => {
    await render(fixtureCards()[0]);
    const opened: string[] = [];
    fixture.componentInstance.openRun.subscribe(id => opened.push(id));

    (fixture.nativeElement.querySelector('[data-testid="deploy-environment-open-run"]') as HTMLButtonElement).click();
    expect(opened).toEqual(['job-b-stage']);
  });

  it('renders without a run or deployment and offers no run action', async () => {
    const data = deployEnvironmentsFixture();
    data.environments[1].history = [];
    data.jobs = [];
    await render(buildEnvironmentCards(data, [], null, null)[1]);

    expect(text('deploy-environment-release')).toBe('Nothing deployed yet');
    expect(text('deploy-environment-age')).toBe('Never');
    expect(text('deploy-environment-last-run')).toBe('No runs yet');
    expect(fixture.nativeElement.querySelector('[data-testid="deploy-environment-open-run"]')).toBeNull();
  });

  describe('accessibility', () => {
    it('labels the card by its environment heading', async () => {
      await render(fixtureCards()[0]);
      const article = fixture.nativeElement.querySelector('article') as HTMLElement;
      const heading = fixture.nativeElement.querySelector(`#${article.getAttribute('aria-labelledby')}`) as HTMLElement;

      expect(heading.tagName).toBe('H3');
      expect(heading.textContent?.trim()).toBe('Staging');
    });

    it('names each action with its environment and keeps them in reading order', async () => {
      await render(fixtureCards()[1]);
      const buttons = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>);

      expect(buttons.map(button => button.getAttribute('aria-label'))).toEqual([
        'Open last run for Production',
        'Review the pending approval for Production',
      ]);
      expect(buttons.every(button => button.type === 'button' && button.tabIndex === 0)).toBe(true);
    });

    it('states facts as a description list, not by colour alone', async () => {
      await render(fixtureCards()[0]);
      const terms = Array.from(fixture.nativeElement.querySelectorAll('dt') as NodeListOf<HTMLElement>).map(dt => dt.textContent?.trim());

      expect(terms).toEqual(['Release', 'Deployed', 'Cards', 'Last run']);
      expect(text('deploy-environment-health')).toBe('Healthy');
    });
  });
});
