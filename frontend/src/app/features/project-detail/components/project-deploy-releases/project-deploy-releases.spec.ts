import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { of } from 'rxjs';
import { ProjectDeployReleasesComponent } from './project-deploy-releases';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { buildEnvironmentCards, type DeployPanel, type Release } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deployReleasesFixture, deploySourcesFixture } from '../../testing/deploy-fixtures';

describe('ProjectDeployReleasesComponent', () => {
  let fixture: ComponentFixture<ProjectDeployReleasesComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function render(panel: DeployPanel<Release[]>): Promise<void> {
    fixture.componentRef.setInput('panel', panel);
    fixture.componentRef.setInput('environments', buildEnvironmentCards(deploySourcesFixture()));
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployReleasesComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: ProjectDeployDataService, useValue: { releaseNotes: () => of({ status: 'unavailable' }) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployReleasesComponent);
  });

  it('shows the loading surface while releases load', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
  });

  it('explains how releases appear when the release service is not on this server', async () => {
    await render({ status: 'unavailable' });
    const text = byTestId('deploy-releases-unavailable')?.textContent ?? '';
    expect(text).toContain('No release records on this server yet');
    expect(text).toContain('Each green build of the integration branch then becomes a release');
  });

  it('distinguishes a failed read and an empty release list', async () => {
    await render({ status: 'error', message: 'invalid deployment state' });
    expect(byTestId('deploy-releases-error')?.textContent).toContain('invalid deployment state');

    await render({ status: 'ready', value: [] });
    expect(byTestId('deploy-releases-error')).toBeNull();
    expect(byTestId('deploy-releases-empty')?.textContent).toContain('No releases yet');
  });

  it('lists releases newest first and details the newest by default', async () => {
    await render({ status: 'ready', value: deployReleasesFixture() });

    const rows = Array.from(host().querySelectorAll('.releases__row')).map(row => row.getAttribute('data-testid'));
    expect(rows).toEqual([`deploy-release-${'c'.repeat(16)}`, `deploy-release-${'b'.repeat(16)}`, `deploy-release-${'a'.repeat(16)}`]);
    expect(byTestId('deploy-release-detail')?.textContent).toContain('Release 2026.10.19.2');
    expect(byTestId('deploy-release-detail')?.textContent).toContain('WEB-35');
    expect(byTestId('deploy-release-live-in')?.textContent?.trim()).toBe('Staging');
    expect(byTestId(`deploy-release-${'c'.repeat(16)}`)?.getAttribute('aria-current')).toBe('true');
  });

  it('selects another release and names the environments it runs in', async () => {
    await render({ status: 'ready', value: deployReleasesFixture() });

    byTestId(`deploy-release-${'a'.repeat(16)}`)!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(byTestId('deploy-release-detail')?.textContent).toContain('Release 2026.10.18.1');
    expect(byTestId('deploy-release-live-in')?.textContent?.trim()).toBe('Production');
    expect(byTestId(`deploy-release-${'c'.repeat(16)}`)?.getAttribute('aria-current')).toBeNull();
  });
});
