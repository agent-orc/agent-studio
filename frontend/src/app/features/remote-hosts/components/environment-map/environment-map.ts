import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { EnvironmentMap, EnvironmentNode } from '../../models/environment-map';
import { buildEnvironmentTopology } from '../../models/environment-topology';

@Component({
  selector: 'app-environment-map',
  standalone: true,
  templateUrl: './environment-map.html',
  styleUrl: './environment-map.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentMapComponent {
  readonly map = input.required<EnvironmentMap>();
  readonly topology = computed(() => buildEnvironmentTopology(this.map()));
  readonly selectHost = output<string>();

  select(node: EnvironmentNode): void {
    if (node.hostId) this.selectHost.emit(node.hostId);
  }
}
