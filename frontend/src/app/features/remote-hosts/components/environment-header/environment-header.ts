import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-environment-header', standalone: true,
  imports: [SegmentedControlComponent],
  templateUrl: './environment-header.html', styleUrl: './environment-header.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHeaderComponent {
  readonly viewOptions = [{ value: 'list', label: 'Hosts', testid: 'environment-list-toggle' }, { value: 'map', label: 'Topology', testid: 'environment-map-toggle' }, { value: 'installation', label: 'Installation', testid: 'environment-installation-toggle' }, { value: 'history', label: 'History', testid: 'environment-history-toggle' }] as const;
  readonly view = input.required<'list' | 'map' | 'installation' | 'history'>();
  readonly retiredCount = input.required<number>();
  readonly loading = input.required<boolean>();
  readonly viewChange = output<'list' | 'map' | 'installation' | 'history'>();
  readonly add = output<void>();
  readonly purge = output<void>();
  readonly reload = output<void>();
}
