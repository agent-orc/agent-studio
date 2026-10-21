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
          environments: (project: string) => project === 'demo' ? oldEnvironments : of([{ id: 'stage', kind: 'stage' }]),
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
      { id: 'preview', kind: 'preview' }, { id: 'stage', kind: 'stage' },
    ]);
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
    (element.querySelector('[data-testid="project-secrets-missing"] button') as HTMLButtonElement).click();
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
});
