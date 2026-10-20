import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { signal } from '@angular/core';
import { AuthSessionState } from '../../../services/auth.service';
import { SecretsService } from '../secrets.service';
import { ProjectSecretsPageComponent } from './project-secrets-page.component';

describe('ProjectSecretsPageComponent', () => {
  it('shows missing references by environment and binds only a reference after approval', () => {
    TestBed.configureTestingModule({
      imports: [ProjectSecretsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: SecretsService, useValue: {
          environments: () => of(['stage', 'prod']), load: vi.fn(), overview: signal(null),
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
    expect(select.textContent).toContain('prod');
    select.value = 'prod';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/secret-bindings/prod').flush({
      project: 'demo', environment: 'prod', bindings: [], ready: false,
      missing: [{ name: 'DATABASE_URL', reference: 'secret://demo/prod/DATABASE_URL', scope: 'demo/prod', reason: 'not-bound' }],
    });
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="project-secrets-missing"]')?.textContent).toContain('not-bound');
    expect(element.querySelector('[data-testid="secrets-panel"]')?.textContent).toContain('secret://demo/prod/NAME');
    (element.querySelector('[data-testid="project-secrets-missing"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="project-secrets-approve"]') as HTMLInputElement).click();
    (element.querySelector('[data-testid="project-secrets-bind"]') as HTMLButtonElement).click();
    const put = http.expectOne('/api/projects/demo/secret-bindings/prod/DATABASE_URL');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ reference: 'secret://demo/prod/DATABASE_URL', approve: true });
    put.flush({ name: 'DATABASE_URL', reference: 'secret://demo/prod/DATABASE_URL', approvedBy: 'owner' });
    http.expectOne('/api/projects/demo/secret-bindings/prod').flush({
      project: 'demo', environment: 'prod', bindings: [], missing: [], ready: true,
    });
    http.verify();
  });
});
