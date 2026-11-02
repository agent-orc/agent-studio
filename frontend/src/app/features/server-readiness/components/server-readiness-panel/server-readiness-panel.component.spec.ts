import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ServerReadinessPanelComponent } from './server-readiness-panel.component';
import { groupReadiness, type ReadinessCheck, type ServerReadinessReport } from '../../models/server-readiness.model';

const check = (patch: Partial<ReadinessCheck>): ReadinessCheck => ({
  id: 'git-identity', title: 'Git identity of the service user', scope: 'server', subject: null,
  state: 'pass', meaning: 'Commits are made as Agent Studio.', remedy: null, ...patch,
});

const report: ServerReadinessReport = {
  computedAt: '2026-11-01T12:04:00Z', state: 'fail', failing: 1, attention: 1,
  checks: [
    check({}),
    check({ id: 'committer-identity', title: 'Committer identity (git commit-tree probe)', state: 'fail',
      meaning: 'git commit-tree failed in a scratch repository (Committer identity unknown).',
      remedy: 'In the stack directory run: docker compose exec api git config --global user.name "Agent Studio"' }),
    check({ id: 'repository-visibility', title: 'Repository visibility known', scope: 'project', subject: 'Demo',
      state: 'warn', meaning: 'Not recorded.', remedy: 'Record it: PUT /api/projects/Demo/repository-visibility' }),
    check({ id: 'execution-host', title: 'Execution host enrolled and seen', scope: 'host', subject: 'kori-review-1',
      meaning: 'Enrolled and last seen 1 min ago.' }),
  ],
};

describe('ServerReadinessPanelComponent', () => {
  async function render() {
    await TestBed.configureTestingModule({
      imports: [ServerReadinessPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ServerReadinessPanelComponent);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), element: fixture.nativeElement as HTMLElement };
  }

  it('shows each problem with its state, meaning and remedy, and the passing lines on request', async () => {
    const { fixture, http, element } = await render();
    http.expectOne('/api/system/readiness').flush(report);
    fixture.detectChanges();

    expect(element.querySelector('h2')!.textContent).toContain('1 failing · 1 need attention · checked 12:04 UTC');
    const failing = element.querySelector('[data-testid="readiness-server:committer-identity:"]')!;
    expect(failing.getAttribute('data-state')).toBe('fail');
    expect(failing.textContent).toContain('Failing');
    expect(failing.textContent).toContain('Committer identity unknown');
    expect(failing.querySelector('code')!.textContent).toContain('git config --global user.name "Agent Studio"');
    expect(element.querySelector('[data-testid="readiness-project:repository-visibility:Demo"]')!.textContent)
      .toContain('Attention');
    expect(element.textContent).toContain('Project Demo');
    expect(element.querySelector('[data-check="git-identity"]')).toBeNull();

    (element.querySelector('[data-testid="server-readiness-passing"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const passing = element.querySelector('[data-testid="readiness-server:git-identity:"]')!;
    expect(passing.textContent).toContain('Passing');
    expect(passing.textContent).toContain('None needed');
    expect(element.querySelector('[data-testid="readiness-host:execution-host:kori-review-1"]')!.textContent)
      .toContain('kori-review-1');
    fixture.destroy();
    http.verify();
  });

  it('rechecks through the POST route and says when every check passes', async () => {
    const { fixture, http, element } = await render();
    http.expectOne('/api/system/readiness').flush({ computedAt: null, state: 'pending', failing: 0, attention: 0, checks: [] });
    fixture.detectChanges();
    expect(element.textContent).toContain('The server is computing its first readiness report.');

    (element.querySelector('[data-testid="server-readiness-recheck"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="server-readiness-recheck"]')!.textContent).toContain('Checking');
    const recheck = http.expectOne('/api/system/readiness/refresh');
    expect(recheck.request.method).toBe('POST');
    recheck.flush({ ...report, state: 'pass', failing: 0, attention: 0, checks: [report.checks[0]] });
    fixture.detectChanges();

    expect(element.querySelector('h2')!.textContent).toContain('All 1 checks pass');
    expect(element.querySelector('[data-testid="server-readiness-all-ready"]')).not.toBeNull();
    fixture.destroy();
    http.verify();
  });

  it('groups server lines first, then hosts, then each project', () => {
    const groups = groupReadiness([report.checks[2], report.checks[3], report.checks[0]]);

    expect(groups.map(group => group.label)).toEqual(['Server', 'Execution hosts', 'Project Demo']);
  });
});
