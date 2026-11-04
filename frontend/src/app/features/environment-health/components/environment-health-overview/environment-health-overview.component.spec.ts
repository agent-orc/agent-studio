import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EnvironmentHealthOverviewComponent, healthIncidentDisplay, healthStatusTone } from './environment-health-overview.component';
import { healthResponse } from '../../../../../testing/environment-health';

describe('EnvironmentHealthOverviewComponent', () => {
  it('lists every environment with uptime, latency and the incident start', async () => {
    await TestBed.configureTestingModule({
      imports: [EnvironmentHealthOverviewComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentHealthOverviewComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/environment/health').flush(healthResponse);
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('1 environments · 1 open incidents');
    const row = element.querySelector('[data-testid="health-overview-Demo-production"]')!;
    expect(row.getAttribute('data-status')).toBe('down');
    expect(row.textContent).toContain('Down');
    expect(row.textContent).toContain('99.80 %');
    expect(row.textContent).toContain('110 ms');
    expect(row.textContent).toContain('since Oct 19, 07:45 UTC · http');
    const status = row.querySelector('[data-testid="health-overview-status"]') as HTMLElement;
    expect(status.dataset['tone']).toBe('danger');
    expect(status.textContent?.trim()).toBe('Down');
    expect((row.querySelector('[data-testid="health-overview-incident"]') as HTMLElement).dataset['tone']).toBe('danger');
    fixture.destroy();
    http.verify();
  });
});

describe('environment health tones', () => {
  it('maps every health status and falls back to neutral', () => {
    expect(['up', 'degraded', 'invalid', 'down', 'unknown', 'new-state'].map(healthStatusTone))
      .toEqual(['success', 'warning', 'warning', 'danger', 'neutral', 'neutral']);
  });

  it('shows an open incident or a watch error as danger and nothing else as neutral', () => {
    const incident = healthResponse.environments.find(item => item.currentIncident)!.currentIncident;
    expect(healthIncidentDisplay({ currentIncident: incident, error: null }).tone).toBe('danger');
    expect(healthIncidentDisplay({ currentIncident: null, error: 'broken declaration' }))
      .toEqual({ tone: 'danger', label: 'broken declaration', detail: 'broken declaration' });
    expect(healthIncidentDisplay({ currentIncident: null, error: null }))
      .toEqual({ tone: 'neutral', label: 'None', detail: null });
  });
});
