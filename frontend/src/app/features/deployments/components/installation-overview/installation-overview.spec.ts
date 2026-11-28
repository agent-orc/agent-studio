import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { InstallationOverviewComponent } from './installation-overview';

describe('InstallationOverviewComponent', () => {
  it('shows stale and missing commits, partial attribution and history from release facts', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(InstallationOverviewComponent);
    fixture.componentRef.setInput('project', 'Studio');
    const component = (id: string, commit: string | null, health: string) => ({ id, name: id, commit, health, machine: 'host' });
    fixture.componentRef.setInput('facts', { version: '1.0', commit: 'abcdef123', timeBasis: 'build-baseline',
      components: [component('API', 'abcdef123', 'Responding'), component('Review', 'abcfed123', 'Reporting')],
      waiting: [], detail: 'Some deliveries could not be resolved.', verification: { state: 'unknown', source: 'No receipt' },
      history: [{ id: 'first', atUtc: '2026-10-01T00:00:00Z', toCommit: 'abcdef123', outcome: 'observed running', cardCount: null, steps: [] }] });
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Review on a different commit');
    expect(root.textContent).toContain('Live time not observed');
    expect(root.textContent).toContain('Some deliveries could not be resolved');
    expect(root.textContent).toContain('Duration not observed');
    expect(root.textContent).toContain('Card count not observed');
    expect(root.textContent).toContain('Observed running');
    fixture.componentRef.setInput('facts', { ...fixture.componentInstance.facts(),
      components: [component('API', 'abcdef123', 'Responding'), component('Review', null, 'Stale heartbeat')] });
    await fixture.whenStable();
    expect(root.textContent).toContain('Commit alignment not fully observed');
  });
});
