import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { environmentHealthTone, repositoryAccessTone, type EnvironmentMap, type EnvironmentNode } from '../../models/environment-map';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { buildEnvironmentTopology } from '../../models/environment-topology';

@Component({
  selector: 'app-environment-map',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './environment-map.html',
  styleUrl: './environment-map.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentMapComponent {
  readonly map = input.required<EnvironmentMap>();
  readonly topology = computed(() => buildEnvironmentTopology(this.map()));
  readonly selectHost = output<string>();
  readonly healthTone = environmentHealthTone;
  readonly accessTone = repositoryAccessTone;

  select(node: EnvironmentNode): void {
    if (node.hostId) this.selectHost.emit(node.hostId);
  }
}
