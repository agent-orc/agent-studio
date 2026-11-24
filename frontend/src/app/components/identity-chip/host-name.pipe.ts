import { Pipe, PipeTransform, inject } from '@angular/core';
import { HostAliasService } from './host-alias.service';

/** `{{ hostId | hostName }}`: the display name or stable alias of a host (AGT-4034). Impure so a rename shows at once. */
@Pipe({ name: 'hostName', standalone: true, pure: false })
export class HostNamePipe implements PipeTransform {
  private readonly aliases = inject(HostAliasService);
  transform(hostId: string | null | undefined, name?: string | null): string {
    return hostId ? this.aliases.display(hostId, name).label : (name ?? '');
  }
}
