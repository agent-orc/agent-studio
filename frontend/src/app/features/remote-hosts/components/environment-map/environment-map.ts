import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { EnvironmentMap } from '../../models/environment-map';
import type { TopologyPoint } from '../../models/environment-topology';
import { buildEnvironmentTopology } from '../../models/environment-topology';

@Component({
  selector: 'app-environment-map',
  standalone: true,
  imports: [],
  templateUrl: './environment-map.html',
  styleUrl: './environment-map.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentMapComponent {
  readonly map = input.required<EnvironmentMap>();
  readonly topology = computed(() => buildEnvironmentTopology(this.map()));
  readonly selectHost = output<string>();

  selectPoint(point: TopologyPoint): void { if (point.hostId) this.selectHost.emit(point.hostId); }
}
