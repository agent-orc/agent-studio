import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it } from 'vitest';
import { DeliverySectionComponent } from './delivery-section';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import type { DeliveryColumn, DeliveryColumnsResponse } from '../../../../models/delivery-column.model';

const task = (key: string): TaskInfo => ({
  id: key.toLowerCase(), taskKey: `demo::${key.toLowerCase()}`, key, displayKey: key, title: `Card ${key}`,
  state: TaskState.Completed, order: 1, agent: 'claude', createdAt: '2026-10-20T12:00:00Z', watchPath: 'demo',
  projectName: 'demo', folderPath: '/tmp', lastActivity: '2026-10-20T12:00:00Z',
}) as TaskInfo;

const column = (id: string, name: string, parts: Partial<DeliveryColumn>): DeliveryColumn => ({
  id, environmentId: id, name, kind: null, order: 0, currentRelease: null, lastRun: null, health: null,
  pendingApproval: null, landed: [], waiting: [], rolledBack: [], ...parts,
});

const response: DeliveryColumnsResponse = {
  projectId: 'demo', releaseDepth: 5, columns: [
    column('integrated', 'Integrated', { landed: [{ key: 'AGT-5', releaseId: null, releaseVersion: null, rolledBack: false }] }),
    column('stage', 'Stage', {
      currentRelease: { id: 'r1', version: '2026.10.20.3', channel: 'default', createdAtUtc: '2026-10-20T08:00:00Z' },
      lastRun: { id: 'd1', releaseId: 'r1', state: 'succeeded', trigger: 'rollback', atUtc: '2026-10-20T09:00:00Z' },
      health: 'healthy',
      landed: [{ key: 'AGT-1', releaseId: 'r1', releaseVersion: '2026.10.20.3', rolledBack: false }],
      waiting: [{ key: 'AGT-4', releaseId: 'r2', releaseVersion: '2026.10.21.1', rolledBack: true }],
    }),
    column('production', 'Production', {
      pendingApproval: { deploymentId: 'd9', releaseId: 'r1', releaseVersion: '2026.10.20.3', requestedAtUtc: '2026-10-21T10:00:00Z' },
      waiting: [{ key: 'AGT-1', releaseId: 'r1', releaseVersion: '2026.10.20.3', rolledBack: false }],
    }),
  ],
};

describe('DeliverySectionComponent', () => {
  afterEach(() => localStorage.removeItem('deliveryFurthestOnly'));

  async function render() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(DeliverySectionComponent);
    fixture.componentRef.setInput('projects', ['demo']);
    fixture.componentRef.setInput('tasks', ['AGT-1', 'AGT-4', 'AGT-5'].map(task));
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/demo/deploy/lanes').flush(response);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  const testIds = (root: HTMLElement, id: string) => [...root.querySelectorAll(`[data-testid="${id}"]`)];

  it('renders environment columns with landed cards, a Waiting divider, waiting cards and header signals', async () => {
    const { root } = await render();
    const titles = [...root.querySelectorAll('.delivery-column__title')].map((el) => el.textContent?.trim());
    expect(titles).toEqual(['Integrated', 'Delivery to Stage', 'Delivery to Production']);

    const stage = root.querySelector('[data-testid="delivery-column-stage"]') as HTMLElement;
    expect(stage.querySelector('[data-testid="delivery-release-version"]')?.textContent).toContain('2026.10.20.3');
    expect(stage.querySelector('[data-testid="delivery-run-state"]')?.textContent).toContain('Rolled back');
    expect(stage.querySelector('[data-testid="delivery-health"]')?.textContent).toContain('Healthy');
    const order = [...stage.querySelectorAll('[data-testid="delivery-landed-card"], [data-testid="delivery-waiting-divider"], [data-testid="delivery-waiting-card"]')]
      .map((el) => el.getAttribute('data-testid'));
    expect(order).toEqual(['delivery-landed-card', 'delivery-waiting-divider', 'delivery-waiting-card']);
    expect(stage.querySelector('[data-testid="delivery-rolled-back"]')?.textContent).toContain('2026.10.21.1');

    const production = root.querySelector('[data-testid="delivery-column-production"]') as HTMLElement;
    expect(production.getAttribute('data-delivery-tone')).toBe('attention');
    expect(production.querySelector('[data-testid="delivery-approval-pill"]')?.textContent).toContain('Approval pending');
  });

  it('keeps the group count equal to the sum of the visible column counts', async () => {
    const { root } = await render();
    const columns = testIds(root, 'delivery-landed-card').length + testIds(root, 'delivery-waiting-card').length;
    const perColumn = [...root.querySelectorAll('[data-testid^="delivery-column-count-"]')]
      .reduce((sum, el) => sum + Number(el.textContent), 0);
    expect(Number(root.querySelector('[data-testid="delivery-group-count"]')?.textContent)).toBe(columns);
    expect(perColumn).toBe(columns);
  });

  it('collapses to the furthest environment and collapses a column body', async () => {
    const { fixture, root } = await render();
    (root.querySelector('[data-testid="delivery-furthest-only"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(testIds(root, 'delivery-landed-card').map((el) => el.getAttribute('data-card-key'))).toEqual(['AGT-5', 'AGT-1']);
    expect(testIds(root, 'delivery-waiting-card').map((el) => el.getAttribute('data-card-key'))).toEqual(['AGT-4']);
    expect(localStorage.getItem('deliveryFurthestOnly')).toBe('true');

    const toggle = root.querySelector('[data-testid="delivery-column-toggle-stage"]') as HTMLButtonElement;
    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="delivery-column-stage"] .delivery-column__body')).toBeNull();
    toggle.click();
  });
});
