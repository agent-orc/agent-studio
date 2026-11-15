import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { WorkspaceOverlaysService } from '../../shell/state/workspace-overlays.service';
import { CredentialInventoryService } from '../credential-inventory.service';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';

/** Current credential alerts shared by the board and operator activity view. */
@Component({
  selector: 'app-credential-alerts',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './credential-alerts.component.html',
  styleUrl: './credential-alerts.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CredentialAlertsComponent {
  private readonly inventory = inject(CredentialInventoryService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  readonly compact = input(false);
  readonly alerts = computed(() => this.inventory.inventory()?.items.filter(item =>
    ['expiring', 'missing', 'unexpected'].includes(item.state)) ?? []);

  constructor() { this.inventory.ensureLoaded(); }

  openInventory(): void { this.overlays.open('credentials'); }
}
