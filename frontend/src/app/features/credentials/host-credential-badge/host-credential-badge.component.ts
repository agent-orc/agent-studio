import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { RemoteHost } from '../../remote-hosts/models/remote-host.model';
import { CredentialInventoryService } from '../credential-inventory.service';

@Component({
  selector: 'app-host-credential-badge',
  standalone: true,
  templateUrl: './host-credential-badge.component.html',
  styleUrl: './host-credential-badge.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostCredentialBadgeComponent {
  private readonly service = inject(CredentialInventoryService);
  readonly host = input.required<RemoteHost>();
  readonly attention = computed(() => {
    const host = this.host();
    const aliases = new Set([host.id, host.clientId, host.capacityHostId ?? '', host.name]
      .filter(Boolean).map(value => value.toLowerCase()));
    return this.service.inventory()?.items.filter(item =>
      aliases.has(item.hostId.toLowerCase()) &&
      ['missing', 'expiring', 'unexpected'].includes(item.state)).length ?? 0;
  });

  constructor() { this.service.ensureLoaded(); }
}
