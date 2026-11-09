import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { OperationsCardLookupService } from '../../state/operations-card-lookup.service';
import { OperationsSecurityComponent, SECURITY_POSTURE_URL } from './operations-security';
import { securityPostureFixture as posture } from '../../testing/security-posture-fixtures';

describe('OperationsSecurityComponent', () => {
  const resolved: { project: string; key: string }[] = [];

  beforeEach(async () => {
    resolved.length = 0;
    await TestBed.configureTestingModule({
      imports: [OperationsSecurityComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: OperationsCardLookupService, useValue: { resolve: (project: string, key: string) => {
          resolved.push({ project, key });
          return of({ jobId: 'job-' + key, watchPath: '/tasks/' + project });
        } } }],
    }).compileComponents();
  });

  async function render(answer: (http: HttpTestingController) => void) {
    const fixture = TestBed.createComponent(OperationsSecurityComponent);
    fixture.detectChanges();
    answer(TestBed.inject(HttpTestingController));
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  const text = (host: HTMLElement, selector: string) => host.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('ranks every entry with its number and lists what needs action soon first', async () => {
    const { host } = await render(http => http.expectOne(SECURITY_POSTURE_URL).flush(posture()));

    const ranked = [...host.querySelectorAll('[data-testid="operations-security-findings"] li')];
    expect(ranked.map(row => row.querySelector('.operations-security__rank')?.textContent)).toEqual(['#1', '#2', '#3', '#4', '#5', '#6']);
    expect(ranked[0].querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('danger');
    expect(ranked[0].textContent).toContain('Critical · 95');
    expect(ranked[3].querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('warning');
    expect(ranked[5].querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('info');
    const soon = [...host.querySelectorAll('[data-testid="operations-security-action-soon"] li')];
    expect(soon.map(row => row.getAttribute('data-testid'))).toEqual(['security-soon-second-factor:u1',
      'security-soon-credentials:token:t1:expiring', 'security-soon-versions:cli:r1:claude']);
    expect(soon[1].textContent).toContain('due 19 Nov 2026');
  });

  it('shows a summary whose severity counts add up to the visible entries', async () => {
    const { host } = await render(http => http.expectOne(SECURITY_POSTURE_URL).flush(posture()));

    const counts = [...host.querySelectorAll('[data-testid="operations-security-summary"] [data-severity] dd')]
      .map(cell => Number(cell.textContent));
    const total = Number(text(host, '[data-testid="operations-security-total"]'));
    expect(counts.reduce((sum, count) => sum + count, 0)).toBe(total);
    expect(host.querySelectorAll('[data-testid="operations-security-findings"] li').length).toBe(total);
    expect(text(host, '[data-testid="operations-security-action-count"]')).toBe('3');
  });

  it('renders each fact source without any secret value', async () => {
    const { host } = await render(http => http.expectOne(SECURITY_POSTURE_URL).flush(posture()));

    expect(text(host, '[data-testid="security-token-t1"]')).toContain('in 10 days');
    expect(text(host, '[data-testid="security-token-t2"]')).toContain('No expiry');
    expect(text(host, '[data-testid="security-token-t2"]')).toContain('Never expires');
    expect(text(host, '[data-testid="security-facts-inventory"]')).toContain('Expires in 6 days');
    expect(text(host, '[data-testid="security-facts-credentials"]')).toContain('Read from the Task Server.');
    expect(text(host, '[data-testid="security-facts-coverage"]')).toBe('1 of 2 active users use a second factor.');
    expect(text(host, '[data-testid="security-user-u1"]')).toContain('No second factor');
    expect(text(host, '[data-testid="security-host-r1"]')).toContain('Ubuntu 24.04.1 LTS');
    expect(text(host, '[data-testid="security-host-r1"]')).toContain('claude 2.1.200 / 2.1.305');
    expect(text(host, '[data-testid="security-host-r1"]')).toContain('Below required');
    expect(text(host, '[data-testid="security-facts-anonymous"]')).toContain('POST /api/auth/login');
    expect(text(host, '[data-testid="security-facts-exposure"]')).toContain('Not visible to the API');
    expect(text(host, '[data-testid="security-facts-anomalies"]')).toContain('Refused request');
    expect(text(host, '[data-testid="security-facts-cards"]')).toContain('Studio · Ready');
  });

  it('opens a security card through the card lookup', async () => {
    const { fixture, host } = await render(http => http.expectOne(SECURITY_POSTURE_URL).flush(posture()));
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(target => opened.push(target));

    host.querySelector<HTMLButtonElement>('[data-testid="security-card-SEC-1"]')!.click();

    expect(resolved).toEqual([{ project: 'Studio', key: 'SEC-1' }]);
    expect(opened).toEqual([{ jobId: 'job-SEC-1', watchPath: '/tasks/Studio' }]);
  });

  it('stays hidden for a caller without the permission', async () => {
    const { host } = await render(http =>
      http.expectOne(SECURITY_POSTURE_URL).flush({ error: 'permission-denied' }, { status: 403, statusText: 'Forbidden' }));

    expect(host.querySelector('[data-testid="operations-security"]')).toBeNull();
  });

  it('names a failed read and loads again on refresh', async () => {
    const { fixture, host } = await render(http =>
      http.expectOne(SECURITY_POSTURE_URL).flush('boom', { status: 500, statusText: 'Server Error' }));
    expect(text(host, '[role="alert"]')).toBe('The security view could not be loaded.');

    host.querySelector<HTMLButtonElement>('[data-testid="operations-security-refresh"]')!.click();
    const empty = { ...posture(), summary: { total: 0, critical: 0, high: 0, medium: 0, low: 0, info: 0, actionSoon: 0 }, findings: [] };
    TestBed.inject(HttpTestingController).expectOne(SECURITY_POSTURE_URL).flush(empty);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(host.querySelector('[data-testid="operations-security-action-soon"]')).toBeNull();
    expect(text(host, '[data-testid="operations-security-clear"]')).toContain('No security entries');
  });
});
