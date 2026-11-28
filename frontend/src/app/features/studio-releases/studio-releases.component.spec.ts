import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { expect, it, vi } from 'vitest';
import { StudioReleasesComponent } from './studio-releases.component';
import { StudioReleasesService, type StudioReleasesView } from './services/studio-releases.service';

it('renders a pending card with missing integration time and the running deployment', async () => {
  const data: StudioReleasesView = {
    project: 'Studio', runningRevision: 'a'.repeat(40), computedAtUtc: '2026-11-27T12:00:00Z', detail: null,
    pending: [{ key: 'WEB-1', title: 'Fix navigation', type: 'bug', href: '#/tasks/WEB-1', integratedAtUtc: null }],
    releases: [{ occurrenceId: 'deploy-1', revision: 'a'.repeat(40), atUtc: '2026-11-27T11:00:00Z',
      observed: true, running: true, cards: [] }],
  };
  const view = vi.fn(() => of(data));
  TestBed.configureTestingModule({
    imports: [StudioReleasesComponent],
    providers: [{ provide: StudioReleasesService, useValue: { view } }],
  });
  const fixture = TestBed.createComponent(StudioReleasesComponent);
  fixture.componentRef.setInput('project', 'Studio');
  fixture.detectChanges();
  const host: HTMLElement = fixture.nativeElement;
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="studio-release-pending"] time')?.textContent)
      .toBe('Integration time unavailable');
  });
  expect(view).toHaveBeenCalledWith('Studio');
  expect(host.querySelector('a')?.getAttribute('href')).toBe('#/tasks/WEB-1');
  const entry = host.querySelector('[data-occurrence-id="deploy-1"]');
  expect(entry?.textContent).toContain('Running');
  expect(entry?.querySelector('time')?.dateTime).toBe(data.releases[0].atUtc);
});
