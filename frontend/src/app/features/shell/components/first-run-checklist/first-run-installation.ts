import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { catchError, map, of, tap, type Observable } from 'rxjs';
import type { AuthStatus } from '../../../../services/auth.service';
import type { InstallationIdentity } from '../../../remote-hosts/models/deployment-checkpoints';
import type { Probe } from './first-run-checklist.models';

/** The monolith has owner auth status but no Task Server installation route. */
export function readInstallationIdentity(
  http: HttpClient, status: AuthStatus | null,
  setMonolithStatus: (probe: Probe<AuthStatus>) => void,
): Observable<Probe<InstallationIdentity>> {
  const identity = status?.profile === 'local'
    ? of({ installationId: 'local', ownerBootstrapped: status.bootstrapRequired === false,
        ownerBootstrapArmed: false } as InstallationIdentity)
    : http.get<InstallationIdentity>('/api/v1/installation');
  return identity.pipe(
    map(value => ({ value, error: false } as Probe<InstallationIdentity>)),
    catchError(error => {
      if (!(error instanceof HttpErrorResponse && error.status === 404 && status?.profile === 'networked')) {
        return of({ value: null, error: true } as Probe<InstallationIdentity>);
      }
      return http.get<AuthStatus>('/api/auth/status').pipe(
        tap(value => setMonolithStatus({ value, error: false })),
        map(() => ({ value: null, error: true } as Probe<InstallationIdentity>)),
        catchError(() => {
          setMonolithStatus({ value: null, error: true });
          return of({ value: null, error: true } as Probe<InstallationIdentity>);
        }));
    }));
}
