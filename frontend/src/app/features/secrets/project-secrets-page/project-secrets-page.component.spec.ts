import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of, Subject } from 'rxjs';
import { signal } from '@angular/core';
import { AuthSessionState } from '../../../services/auth.service';
import { SecretsService } from '../secrets.service';
import { ProjectSecretsPageComponent } from './project-secrets-page.component';

describe('ProjectSecretsPageComponent', () => {
  it('cancels the environment lookup when the project changes', () => {
    const oldEnvironments = new Subject<readonly { id: string; kind: string }[]>();
    TestBed.configureTestingModule({
      imports: [ProjectSecretsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: SecretsService, useValue: {
          environments: (project: string) => project === 'demo' ? oldEnvironments : of([{ id: 'stage', kind: 'stage', name: 'staging' }]),
          load: vi.fn(), overview: signal(null), state: signal('ready'), error: signal(null),
        } },
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ProjectSecretsPageComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/secret-bindings/preview');

    fixture.componentRef.setInput('projectName', 'other');
    fixture.detectChanges();
    http.expectOne('/api/projects/other/secret-bindings/preview');
    expect(oldEnvironments.observed).toBe(false);
    expect(fixture.componentInstance.environments()).toEqual([
      { id: 'preview', kind: 'preview' }, { id: 'stage', kind: 'stage', name: 'staging' },
    ]);
    fixture.detectChanges();
    const options = Array.from((fixture.nativeElement as HTMLElement)
      .querySelectorAll('[data-testid="project-secrets-environment"] option'));
    expect(options.map(option => [option.getAttribute('value'), option.textContent?.trim()]))
      .toEqual([['preview', 'preview'], ['stage', 'staging']]);
  });

  it('ignores preflight responses from an earlier environment or request', () => {
    TestBed.configureTestingModule({
      imports: [ProjectSecretsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: SecretsService, useValue: {
          environments: () => of([{ id: 'stage', kind: 'stage' }]),
          load: vi.fn(), overview: signal(null), state: signal('ready'), error: signal(null),
        } },
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ProjectSecretsPageComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const oldPreview = http.expectOne('/api/projects/demo/secret-bindings/preview');
    http.expectOne('/api/projects/demo/deploy/secret-readiness').flush({ environments: [] });
    const element = fixture.nativeElement as HTMLElement;

    fixture.componentInstance.selectEnvironment('stage');
    fixture.detectChanges();
    const stage = http.expectOne('/api/projects/demo/secret-bindings/stage');
    oldPreview.flush({ project: 'demo', environment: 'preview', ready: true, missing: [],
      bindings: [{ name: 'OLD', reference: 'secret://demo/preview/OLD', approvedBy: null }] });
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="project-secrets-preflight"]')).toBeNull();
    expect(element.textContent).not.toContain('secret://demo/preview/OLD');

    fixture.componentInstance.refresh();
    const latestStage = http.expectOne('/api/projects/demo/secret-bindings/stage');
    latestStage.flush({ project: 'demo', environment: 'stage', ready: true, missing: [],
      bindings: [{ name: 'CURRENT', reference: 'secret://demo/stage/CURRENT', approvedBy: null }] });
    stage.flush({ project: 'demo', environment: 'stage', ready: true, missing: [],
      bindings: [{ name: 'STALE', reference: 'secret://demo/stage/STALE', approvedBy: null }] });
    fixture.detectChanges();
    expect(element.textContent).toContain('secret://demo/stage/CURRENT');
    expect(element.textContent).not.toContain('secret://demo/stage/STALE');
    http.verify();
  });

  it('shows missing references by environment and binds only a reference after approval', () => {
    TestBed.configureTestingModule({
      imports: [ProjectSecretsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: SecretsService, useValue: {
          environments: () => of([{ id: 'stage', kind: 'stage' }, { id: 'live-eu', kind: 'production' }]),
          load: vi.fn(), overview: signal(null),
          state: signal('ready'), error: signal(null),
        } },
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ProjectSecretsPageComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/secret-bindings/preview').flush({
      project: 'demo', environment: 'preview', bindings: [], missing: [], ready: true,
    });
    http.expectOne('/api/projects/demo/deploy/secret-readiness').flush({ environments: [
      { environmentId: 'stage', ready: true }, { environmentId: 'live-eu', ready: true },
    ] });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const select = element.querySelector('[data-testid="project-secrets-environment"]') as HTMLSelectElement;
    expect(select.textContent).toContain('live-eu');
    select.value = 'live-eu';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/secret-bindings/live-eu').flush({
      project: 'demo', environment: 'live-eu', bindings: [], ready: false,
      missing: [{ name: 'DATABASE_URL', reference: 'secret://demo/live-eu/DATABASE_URL', scope: 'demo/live-eu', reason: 'not-bound' }],
    });
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="project-secrets-missing"]')?.textContent).toContain('not-bound');
    expect(element.querySelector('[data-testid="secrets-panel"]')?.textContent).toContain('secret://demo/live-eu/NAME');
    (element.querySelector('[data-testid="project-secrets-edit-DATABASE_URL"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="project-secrets-approve"]') as HTMLInputElement).click();
    (element.querySelector('[data-testid="project-secrets-bind"]') as HTMLButtonElement).click();
    const put = http.expectOne('/api/projects/demo/secret-bindings/live-eu/DATABASE_URL');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ reference: 'secret://demo/live-eu/DATABASE_URL', approve: true });
    put.flush({ name: 'DATABASE_URL', reference: 'secret://demo/live-eu/DATABASE_URL', approvedBy: 'owner' });
    http.expectOne('/api/projects/demo/secret-bindings/live-eu').flush({
      project: 'demo', environment: 'live-eu', bindings: [], missing: [], ready: true,
    });
    http.verify();
  });

  function configure(environments: readonly { id: string; kind: string }[]) {
    TestBed.configureTestingModule({
      imports: [ProjectSecretsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: SecretsService, useValue: {
          environments: () => of(environments),
          load: vi.fn(), overview: signal(null), state: signal('ready'), error: signal(null),
        } },
      ],
    });
    return TestBed.inject(HttpTestingController);
  }

  it('opens on the environment whose next deploy misses a secret and binds a listed reference in one action (F14)', () => {
    const http = configure([{ id: 'stage', kind: 'stage' }, { id: 'production', kind: 'production' }]);
    const fixture = TestBed.createComponent(ProjectSecretsPageComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/secret-bindings/preview');
    http.expectOne('/api/projects/demo/deploy/secret-readiness').flush({ environments: [
      { environmentId: 'stage', ready: false }, { environmentId: 'production', ready: false },
    ] });
    fixture.detectChanges();
    expect(fixture.componentInstance.environment()).toBe('stage');
    http.expectOne('/api/projects/demo/secret-bindings/stage').flush({
      project: 'demo', environment: 'stage', bindings: [], ready: false,
      missing: [{ name: 'DATABASE_URL', reference: 'secret://demo/stage/DATABASE_URL', scope: 'demo/stage', reason: 'not-bound' }],
    });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const bind = element.querySelector('[data-testid="project-secrets-bind-DATABASE_URL"]') as HTMLButtonElement;
    expect(bind.textContent).toContain('Bind DATABASE_URL');
    bind.click();
    const put = http.expectOne('/api/projects/demo/secret-bindings/stage/DATABASE_URL');
    expect(put.request.body).toEqual({ reference: 'secret://demo/stage/DATABASE_URL', approve: false });
    put.flush({ name: 'DATABASE_URL', reference: 'secret://demo/stage/DATABASE_URL', approvedBy: null });
    http.expectOne('/api/projects/demo/secret-bindings/stage').flush({
      project: 'demo', environment: 'stage', bindings: [], missing: [], ready: true,
    });
    http.verify();
  });

  it('opens on the environment a Deploy Bind action named and approves a Production binding with the bind (F14)', () => {
    const http = configure([{ id: 'stage', kind: 'stage' }, { id: 'production', kind: 'production' }]);
    const fixture = TestBed.createComponent(ProjectSecretsPageComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.componentRef.setInput('initialEnvironment', 'production');
    fixture.detectChanges();
    http.expectNone('/api/projects/demo/deploy/secret-readiness');
    expect(fixture.componentInstance.environment()).toBe('production');
    http.match('/api/projects/demo/secret-bindings/preview');
    http.expectOne('/api/projects/demo/secret-bindings/production').flush({
      project: 'demo', environment: 'production', bindings: [], ready: false,
      missing: [
        { name: 'DATABASE_URL', reference: 'secret://demo/production/DATABASE_URL', scope: 'demo/production', reason: 'not-bound' },
        { name: 'API_KEY', reference: 'secret://demo/production/API_KEY', scope: 'demo/production', reason: 'not-found' },
      ],
    });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    // A missing stored value needs the secret, not a binding.
    expect(element.querySelector('[data-testid="project-secrets-bind-API_KEY"]')).toBeNull();
    const bind = element.querySelector('[data-testid="project-secrets-bind-DATABASE_URL"]') as HTMLButtonElement;
    expect(bind.textContent).toContain('Bind and approve DATABASE_URL');
    bind.click();
    const put = http.expectOne('/api/projects/demo/secret-bindings/production/DATABASE_URL');
    expect(put.request.body).toEqual({ reference: 'secret://demo/production/DATABASE_URL', approve: true });
  });
});
