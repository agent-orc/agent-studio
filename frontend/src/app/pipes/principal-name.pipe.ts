import { Pipe, PipeTransform, inject } from '@angular/core';
import { ClientService } from '../services/client.service';

/**
 * Display name of a principal id (`usr_…`, `local-default`) for history rows
 * that record who requested or approved something. Unknown ids keep their
 * value. Impure because the user directory loads after the first render.
 */
@Pipe({ name: 'principalName', standalone: true, pure: false })
export class PrincipalNamePipe implements PipeTransform {
  private readonly clients = inject(ClientService);

  transform(id: string | null | undefined): string | null {
    return principalName(this.clients, id);
  }
}

export function principalName(clients: ClientService, id: string | null | undefined): string | null {
  if (!id) return null;
  if (id.startsWith('usr_')) clients.loadHumanNames();
  return clients.resolve(id).displayName;
}
