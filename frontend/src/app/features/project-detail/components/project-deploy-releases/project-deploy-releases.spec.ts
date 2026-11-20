import { provideZonelessChangeDetection } from '@angular/core';
import { vi } from 'vitest';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { ProjectDeployReleasesComponent, verdictTone } from './project-deploy-releases';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { QualityStudioService } from '../../../../services/quality-studio.service';
import { buildEnvironmentCards, type DeployPanel, type IntegrationReleaseStatus, type Release } from '../../state/deploy-area.model';
import { DEPLOY_FIXTURE_NOW, deployReleasesFixture, deploySourcesFixture } from '../../testing/deploy-fixtures';

describe('ProjectDeployReleasesComponent', () => {
  let fixture: ComponentFixture<ProjectDeployReleasesComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function render(panel: DeployPanel<Release[]>): Promise<void> {
    fixture.componentRef.setInput('projectName', 'Web Site');
    fixture.componentRef.setInput('panel', panel);
    fixture.componentRef.setInput('environments', buildEnvironmentCards(deploySourcesFixture()));
    fixture.componentRef.setInput('now', DEPLOY_FIXTURE_NOW);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  let data: {
    releaseNotes: () => unknown;
    secretReadiness: () => unknown;
    integrationRelease: ReturnType<typeof vi.fn>;
    createIntegrationRelease: ReturnType<typeof vi.fn>;
  };
  const status = (value: Partial<IntegrationReleaseStatus>): IntegrationReleaseStatus => ({
    project: 'Web Site', state: 'waiting-for-gate', message: '', nextStep: null, commit: 'e'.repeat(40),
    releaseId: null, canCreate: false, ...value,
  });

  beforeEach(async () => {
    data = {
      releaseNotes: () => of({ status: 'unavailable' }),
      secretReadiness: () => of({ status: 'unavailable' }),
      integrationRelease: vi.fn(() => of({ status: 'unavailable' })),
      createIntegrationRelease: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [ProjectDeployReleasesComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: QualityStudioService, useValue: { openRange: () => undefined } },
        { provide: ProjectDeployDataService, useValue: data }],
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
    const releases = deployReleasesFixture();
    releases[0].preDeliveryVerdict = { project: 'Web Site', commitSha: 'c'.repeat(40),
      commandDigest: 'd'.repeat(64), status: 'green', finishedAtUtc: '2026-10-19T12:00:00Z' };
    await render({ status: 'ready', value: releases });

    const rows = Array.from(host().querySelectorAll('.releases__row')).map(row => row.getAttribute('data-testid'));
    expect(rows).toEqual([`deploy-release-${'c'.repeat(16)}`, `deploy-release-${'b'.repeat(16)}`, `deploy-release-${'a'.repeat(16)}`]);
    expect(byTestId('deploy-release-detail')?.textContent).toContain('Release 2026.10.19.2');
    expect(byTestId('deploy-release-detail')?.textContent).toContain('WEB-35');
    expect(byTestId('deploy-release-live-in')?.textContent?.trim()).toBe('Staging');
    expect(byTestId('deploy-release-verdict')?.textContent?.replace(/\s+/g, ' ')).toContain(`Full suite: green · SHA ${'c'.repeat(40)}`);
    expect(byTestId('deploy-release-verdict-state')?.dataset['tone']).toBe('success');
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

  it('maps the full-suite verdict onto a tone', () => {
    expect(verdictTone('green')).toBe('success');
    expect(verdictTone('red')).toBe('danger');
    expect(verdictTone('later')).toBe('neutral');
  });

  it('reconciles Quality counts with card rows and opens the release range', async () => {
    const releases = deployReleasesFixture();
    const release = { ...releases[0], cardKeys: ['WEB-35', 'WEB-36', 'WEB-37'] };
    releases[0] = release;
    release.quality = {
      cards: [{ key: 'WEB-35', verdict: 'pass' }, { key: 'WEB-36', verdict: 'concerns' },
        { key: 'WEB-37', verdict: 'block' }],
      pass: 1, concerns: 1, block: 1, notReviewed: 0, worstVerdict: 'block',
      rangeVerdict: 'not-reviewed',
      range: { repositoryId: 'repo', baseSha: 'b'.repeat(40), headSha: release.commitSha, requiresConfirmation: false },
      rangeUnavailableReason: null,
    };
    const open = vi.spyOn(TestBed.inject(QualityStudioService), 'openRange');
    await render({ status: 'ready', value: releases });
    expect(byTestId('deploy-release-quality-counts')?.textContent).toContain('1 pass · 1 concerns · 1 block · 0 not reviewed');
    expect(host().querySelectorAll('.releases__quality-cards li').length).toBe(3);
    expect(byTestId('deploy-release-range-verdict')?.textContent).toContain('Not reviewed');
    byTestId('deploy-release-open-quality')!.click();
    expect(open).toHaveBeenCalledWith('Web Site', 'repo', 'b'.repeat(40), release.commitSha);
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('requires confirmation before opening the first release root range', async () => {
    const releases = deployReleasesFixture();
    const first = releases[2];
    first.quality = {
      cards: [], pass: 0, concerns: 0, block: 0, notReviewed: 0,
      worstVerdict: 'not-reviewed', rangeVerdict: 'not-reviewed',
      range: { repositoryId: 'repo', baseSha: '0'.repeat(40), headSha: first.commitSha,
        requiresConfirmation: true }, rangeUnavailableReason: null,
    };
    const open = vi.spyOn(TestBed.inject(QualityStudioService), 'openRange');
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    await render({ status: 'ready', value: releases });
    byTestId(`deploy-release-${first.id}`)!.click();
    fixture.detectChanges();
    byTestId('deploy-release-open-quality')!.click();
    expect(confirm).toHaveBeenCalled();
    expect(open).not.toHaveBeenCalled();
    confirm.mockReturnValue(true);
    byTestId('deploy-release-open-quality')!.click();
    expect(open).toHaveBeenCalledWith('Web Site', 'repo', '0'.repeat(40), first.commitSha);
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('names what creates the next release instead of promising a build', async () => {
    data.integrationRelease.mockReturnValue(of({ status: 'ready', value: status({
      message: 'The tip eeeeeeee of main has no green gate yet.',
      nextStep: 'Studio creates the release as soon as the gate on this commit passes.',
    }) }));
    await render({ status: 'ready', value: [] });

    expect(data.integrationRelease).toHaveBeenCalledWith('Web Site');
    expect(byTestId('deploy-next-release')?.dataset['state']).toBe('waiting-for-gate');
    expect(byTestId('deploy-next-release-message')?.textContent).toContain('has no green gate yet');
    expect(byTestId('deploy-next-release-step')?.textContent).toContain('as soon as the gate on this commit passes');
    expect(byTestId('deploy-next-release-create')).toBeNull();
    expect(byTestId('deploy-releases-empty')?.textContent).not.toContain('next green build');
  });

  it('shows the reason and next step of a blocked release and retries it as one action', async () => {
    data.integrationRelease.mockReturnValue(of({ status: 'ready', value: status({
      state: 'blocked', canCreate: true, message: 'Commit eeeeeeee has no site.', nextStep: 'Commit the static bundle under site.',
    }) }));
    data.createIntegrationRelease.mockReturnValue(of(status({
      state: 'released', message: 'Release 2026.11.16.1 is the tip eeeeeeee of main; Staging is queued.', releaseId: 'r1',
    })));
    await render({ status: 'ready', value: [] });
    let released = 0;
    fixture.componentInstance.released.subscribe(() => released++);

    expect(byTestId('deploy-next-release-state')?.dataset['tone']).toBe('warning');
    expect(byTestId('deploy-next-release-step')?.textContent).toContain('Commit the static bundle');
    byTestId('deploy-next-release-create')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(data.createIntegrationRelease).toHaveBeenCalledWith('Web Site');
    expect(byTestId('deploy-next-release')?.dataset['state']).toBe('released');
    expect(byTestId('deploy-next-release-message')?.textContent).toContain('Staging is queued');
    expect(byTestId('deploy-next-release-create')).toBeNull();
    expect(released).toBe(1);
  });

  it('keeps the action and shows the server refusal when creation fails', async () => {
    data.integrationRelease.mockReturnValue(of({ status: 'ready', value: status({ state: 'ready', canCreate: true,
      message: 'The tip eeeeeeee of main passed its gate and has no release yet.' }) }));
    data.createIntegrationRelease.mockReturnValue(throwError(() => new HttpErrorResponse({
      status: 403, error: { error: 'environment-scope-denied' } })));
    await render({ status: 'ready', value: [] });

    byTestId('deploy-next-release-create')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(byTestId('deploy-next-release-error')?.textContent).toContain('environment-scope-denied');
    expect(byTestId('deploy-next-release-create')).not.toBeNull();
  });
});
