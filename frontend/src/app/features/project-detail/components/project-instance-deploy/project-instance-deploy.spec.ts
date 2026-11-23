import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ProjectInstanceDeployComponent } from './project-instance-deploy';
import { INSTANCE_FIXTURE_NOW, instanceDeployFixture } from '../../testing/instance-deploy-fixtures';
import type { DeployPanel } from '../../state/deploy-area.model';
import { instanceRunLabel, type InstanceDeployView } from '../../state/instance-deploy.model';

async function render(panel: DeployPanel<InstanceDeployView>): Promise<ComponentFixture<ProjectInstanceDeployComponent>> {
  await TestBed.configureTestingModule({
    imports: [ProjectInstanceDeployComponent], providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectInstanceDeployComponent);
  fixture.componentRef.setInput('panel', panel);
  fixture.componentRef.setInput('now', INSTANCE_FIXTURE_NOW);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

const text = (fixture: ComponentFixture<unknown>, id: string) =>
  (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('ProjectInstanceDeployComponent', () => {
  it('shows the live release, the cards it brought, and what the next deploy brings in order', async () => {
    const fixture = await render({ status: 'ready', value: instanceDeployFixture() });
    expect(text(fixture, 'instance-deploy-version')).toBe('0.9.5-5faca674a');
    expect(text(fixture, 'instance-deploy-commit')).toBe('5faca674a');
    expect(text(fixture, 'instance-deploy-live-since')).toBe('2026-11-16 03:35 UTC · 5 h ago');
    expect(text(fixture, 'instance-deploy-brought')).toContain('AGT-3794');
    expect(text(fixture, 'instance-deploy-brought')).toContain('AGT-3839');
    const pending = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll('[data-testid="instance-deploy-pending"] .instance-deploy__key')].map(item => item.textContent);
    expect(pending).toEqual(['AGT-3861', 'AGT-3870', 'AGT-3879']);
    expect(text(fixture, 'instance-deploy-run-state')).toContain('Last deploy succeeded');
  });

  it('opens a card from either list', async () => {
    const fixture = await render({ status: 'ready', value: instanceDeployFixture() });
    const opened: string[] = [];
    fixture.componentInstance.openCard.subscribe(key => opened.push(key));
    const buttons = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.instance-deploy__card');
    buttons[0].click();
    buttons[buttons.length - 1].click();
    expect(opened).toEqual(['AGT-3794', 'AGT-3879']);
  });

  it('names the step a failed or stalled run stopped at', async () => {
    const failed = await render({ status: 'ready', value: instanceDeployFixture('failed') });
    expect(text(failed, 'instance-deploy-run-state')).toContain('Deploy failed at host:runner-1');
    const state = (failed.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-testid="instance-deploy-run-state"]');
    expect(state?.dataset['tone']).toBe('danger');
    const stalled = instanceDeployFixture('stalled').latestRun!;
    expect(instanceRunLabel(stalled, 30)).toBe('Deploy stalled after host:runner-1 · no report for 30 min');
  });

  it('renders nothing for a project that is not the instance source', async () => {
    const fixture = await render({ status: 'unavailable' });
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="instance-deploy"]')).toBeNull();
  });

  it('says where an empty list comes from', async () => {
    const view = instanceDeployFixture();
    const fixture = await render({ status: 'ready', value: {
      ...view, notLiveYet: [], live: { ...view.live!, cards: [], fromVersionRoute: true },
      latestRun: { ...view.latestRun!, fromVersionRoute: true, steps: [] },
    } });
    expect(text(fixture, 'instance-deploy-brought')).toContain('no previous release to compare with');
    expect(text(fixture, 'instance-deploy-pending')).toContain('Nothing accepted on origin/develop after the live commit');
    expect(text(fixture, 'instance-deploy-version-route-note')).toContain('Earlier deploys are not recorded');
    expect(text(fixture, 'instance-deploy-run-state')).toContain('Recorded from the running version');
  });
});
