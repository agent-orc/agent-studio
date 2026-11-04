import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EnvironmentHealthPanelComponent, healthStatusTone } from './environment-health-panel.component';
import { healthResponse } from '../../../../../testing/environment-health';

describe('EnvironmentHealthPanelComponent', () => {
  it('shows uptime windows, latency, the open incident and a close certificate expiry', async () => {
    await TestBed.configureTestingModule({
      imports: [EnvironmentHealthPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentHealthPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo%20Project/environments/health').flush(healthResponse);
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    const environment = element.querySelector('[data-testid="environment-health-production"]')!;
    expect(environment.getAttribute('data-status')).toBe('down');
    expect(environment.textContent).toContain('98.61 %');
    expect(environment.textContent).toContain('99.95 %');
    expect(environment.textContent).toContain('240 ms');
    const incident = element.querySelector('[data-testid="environment-health-incident"]')!.textContent!;
    expect(incident).toMatch(/Oct 19, 2026, 7:45:00\sAM UTC/);
    expect(incident).toContain('15 failed probes');
    expect(incident).toContain('health-production-202610190745');
    expect(element.querySelector('[data-testid="environment-health-check-http"]')!.textContent).toContain('timeout');
    expect(element.querySelector('[data-testid="environment-health-check-tls"]')!.textContent)
      .toContain('Certificate expires in 10 days');
    const tone = (root: Element | null, id: string) => root?.querySelector<HTMLElement>(`[data-testid="${id}"]`)?.dataset['tone'];
    expect(tone(environment, 'environment-health-status')).toBe('danger');
    const http1 = element.querySelector('[data-testid="environment-health-check-http"]');
    expect(tone(http1, 'environment-health-check-status')).toBe('danger');
    expect(tone(http1, 'environment-health-last-result')).toBe('danger');
    const tls = element.querySelector('[data-testid="environment-health-check-tls"]');
    expect(tone(tls, 'environment-health-certificate')).toBe('warning');
    expect(tone(tls, 'environment-health-last-result')).toBe('success');

    element.querySelector<HTMLButtonElement>('.environment-health__header button')!.click();
    http.expectOne('/api/projects/Demo%20Project/environments/health').flush({ ...healthResponse, environments: [] });
    fixture.detectChanges();
    expect(element.textContent).toContain('No environments are declared');
    http.verify();
  });

  it('names a broken declaration on its environment', async () => {
    await TestBed.configureTestingModule({
      imports: [EnvironmentHealthPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentHealthPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const broken = { ...healthResponse.environments[0], environmentId: 'stage', status: 'invalid' as const, checks: [],
      currentIncident: null, error: 'stage.json: health check intervalSeconds must be an integer.' };
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/environments/health')
      .flush({ ...healthResponse, environments: [broken] });
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-testid="environment-health-stage"]')!.textContent).toContain('Misconfigured');
    expect(element.querySelector('[data-testid="environment-health-error"]')!.textContent)
      .toContain('intervalSeconds must be an integer');
    expect(element.querySelector<HTMLElement>('[data-testid="environment-health-status"]')?.dataset['tone']).toBe('warning');
  });

  it('maps every health status onto a tone', () => {
    const matrix: Record<string, string> = { up: 'success', degraded: 'warning', invalid: 'warning', down: 'danger', unknown: 'neutral', later: 'neutral' };
    for (const [status, tone] of Object.entries(matrix)) expect(healthStatusTone(status)).toBe(tone);
  });
});
