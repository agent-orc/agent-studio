import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EnvironmentHealthOverviewComponent } from './environment-health-overview.component';
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
    fixture.destroy();
    http.verify();
  });
});
