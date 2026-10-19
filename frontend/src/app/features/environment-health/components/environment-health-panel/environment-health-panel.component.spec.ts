import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EnvironmentHealthPanelComponent } from './environment-health-panel.component';
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
  });
});
