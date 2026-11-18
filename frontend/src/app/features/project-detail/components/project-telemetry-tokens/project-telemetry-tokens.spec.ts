import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { AuthSessionState } from '../../../../services/auth.service';
import { SecretsService } from '../../../secrets/secrets.service';
import { ProjectTelemetryTokensComponent } from './project-telemetry-tokens';
import type { TelemetryToken } from './project-telemetry-tokens.model';

const PATH = '/api/projects/demo/environments/stage/telemetry/tokens';
const token = (overrides: Partial<TelemetryToken>): TelemetryToken => ({
  id: 'one', name: 'Studio deploy', project: 'demo', environment: 'stage', signals: ['metrics', 'logs'],
  createdAt: '2026-11-16T08:00:00Z', createdBy: 'studio:deploy', expiresAt: null,
  lastUsedAt: '2026-11-16T09:30:00Z', revokedAt: null, ...overrides,
});

function setup(role = 'owner') {
  TestBed.configureTestingModule({
    imports: [ProjectTelemetryTokensComponent],
    providers: [provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthSessionState, useValue: { status: signal({ user: { role } }) } },
      { provide: SecretsService, useValue: {
        environments: () => of([{ id: 'stage', kind: 'stage' }, { id: 'production', kind: 'production' }]),
      } },
    ],
  });
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(ProjectTelemetryTokensComponent);
  fixture.componentRef.setInput('projectName', 'demo');
  fixture.detectChanges();
  return { http, fixture, host: fixture.nativeElement as HTMLElement };
}

describe('ProjectTelemetryTokensComponent', () => {
  it('lists the first environment with last use and labels the deploy-managed token', () => {
    const { http, fixture, host } = setup();
    http.expectOne(PATH).flush([token({}), token({ id: 'two', name: 'old', createdBy: 'owner', lastUsedAt: null,
      revokedAt: '2026-11-16T10:00:00Z' })]);
    fixture.detectChanges();

    const managed = host.querySelector<HTMLElement>('[data-testid="telemetry-token-one"]')!;
    expect(managed.dataset['state']).toBe('active');
    expect(managed.querySelector('[data-testid="telemetry-token-managed"]')?.textContent).toContain('Managed by deploys');
    expect(managed.querySelector('[data-testid="telemetry-token-last-seen"]')?.textContent).toContain('Nov 16, 2026');
    const revoked = host.querySelector<HTMLElement>('[data-testid="telemetry-token-two"]')!;
    expect(revoked.dataset['state']).toBe('revoked');
    expect(revoked.querySelector('[data-testid="telemetry-token-revoke"]')).toBeNull();
    expect(revoked.querySelector('[data-testid="telemetry-token-last-seen"]')?.textContent?.trim()).toBe('Never');
  });

  it('shows the created token once and forgets it on done or an environment switch', () => {
    const { http, fixture, host } = setup();
    http.expectOne(PATH).flush([]);
    const secret = ['otl', 'new', 'x'.repeat(8)].join('.');
    fixture.componentInstance.name.set('shop-api');
    fixture.componentInstance.logs.set(false);
    fixture.componentInstance.create();
    const post = http.expectOne(request => request.method === 'POST' && request.url === PATH);
    expect(post.request.body).toEqual({ name: 'shop-api', signals: ['metrics'], expiresAt: null });
    post.flush({ token: token({ id: 'new', name: 'shop-api', createdBy: 'owner' }), secret,
      otlpEndpoint: 'https://studio.example/api/telemetry/otlp', otlpHeaders: `Authorization=Bearer ${secret}` });
    http.expectOne(PATH).flush([token({ id: 'new', name: 'shop-api', createdBy: 'owner' })]);
    fixture.detectChanges();

    const settings = host.querySelector('[data-testid="telemetry-token-settings"]')!.textContent!;
    expect(settings).toContain('OTEL_EXPORTER_OTLP_ENDPOINT=https://studio.example/api/telemetry/otlp');
    expect(settings).toContain('OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf');
    expect(settings).toContain(`OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ${secret}`);

    host.querySelector<HTMLButtonElement>('[data-testid="telemetry-token-dismiss"]')!.click();
    fixture.detectChanges();
    expect(host.textContent).not.toContain(secret);

    fixture.componentInstance.created.set({ token: token({}), secret, otlpEndpoint: 'x', otlpHeaders: 'y' });
    fixture.componentInstance.selectEnvironment('production');
    http.expectOne('/api/projects/demo/environments/production/telemetry/tokens').flush([]);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="telemetry-token-created"]')).toBeNull();
    expect(host.querySelector('[data-testid="telemetry-tokens-empty"]')?.textContent).toContain('production');
  });

  it('revokes only after the confirming click and reloads the list', () => {
    const { http, fixture, host } = setup();
    http.expectOne(PATH).flush([token({})]);
    fixture.detectChanges();

    host.querySelector<HTMLButtonElement>('[data-testid="telemetry-token-revoke"]')!.click();
    fixture.detectChanges();
    http.expectNone(request => request.method === 'DELETE');
    host.querySelector<HTMLButtonElement>('[data-testid="telemetry-token-confirm-revoke"]')!.click();
    http.expectOne(request => request.method === 'DELETE' && request.url === `${PATH}/one`).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(PATH).flush([token({ revokedAt: '2026-11-16T10:00:00Z' })]);
    fixture.detectChanges();
    expect(host.querySelector<HTMLElement>('[data-testid="telemetry-token-one"]')!.dataset['state']).toBe('revoked');
  });

  it('ignores a token list that arrives after the environment changed', () => {
    const { http, fixture, host } = setup();
    const stale = http.expectOne(PATH);
    fixture.componentInstance.selectEnvironment('production');
    http.expectOne('/api/projects/demo/environments/production/telemetry/tokens').flush([]);
    stale.flush([token({})]);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="telemetry-token-one"]')).toBeNull();
  });

  it('hides create and revoke from viewers', () => {
    const { http, fixture, host } = setup('viewer');
    http.expectOne(PATH).flush([token({})]);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="telemetry-token-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="telemetry-token-revoke"]')).toBeNull();
  });
});
