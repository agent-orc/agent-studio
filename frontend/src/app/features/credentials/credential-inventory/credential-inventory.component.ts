import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CredentialInventoryService } from '../credential-inventory.service';
import { WorkspaceOverlaysService } from '../../shell/state/workspace-overlays.service';

@Component({
  selector: 'app-credential-inventory',
  standalone: true,
  templateUrl: './credential-inventory.component.html',
  styleUrl: './credential-inventory.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CredentialInventoryComponent {
  readonly service = inject(CredentialInventoryService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  readonly host = signal('all');
  readonly kind = signal('all');
  readonly state = signal('all');
  readonly hosts = computed(() => [...new Set(this.service.inventory()?.items.map(item => item.hostId) ?? [])].sort());
  readonly kinds = computed(() => [...new Set(this.service.inventory()?.items.map(item => item.kind) ?? [])].sort());
  readonly items = computed(() => (this.service.inventory()?.items ?? []).filter(item =>
    (this.host() === 'all' || item.hostId === this.host()) &&
    (this.kind() === 'all' || item.kind === this.kind()) &&
    (this.state() === 'all' || item.state === this.state())));
  readonly attention = computed(() => this.service.inventory()?.items.filter(item =>
    item.state === 'missing' || item.state === 'expiring' || item.state === 'unexpected').length ?? 0);

  constructor() { this.service.load(); }

  openActions(category: 'provider' | 'repository' | 'service' | 'human'): void {
    this.overlays.select(category === 'service' || category === 'human' ? 'task-server' : 'remote-hosts');
  }
}
