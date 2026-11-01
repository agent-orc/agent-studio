import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-environment-header', standalone: true,
  imports: [SegmentedControlComponent],
  templateUrl: './environment-header.html', styleUrl: './environment-header.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHeaderComponent {
  readonly viewOptions = [{ value: 'list', label: 'List', testid: 'environment-list-toggle' }, { value: 'map', label: 'Map', testid: 'environment-map-toggle' }] as const;
  readonly view = input.required<'list' | 'map'>();
  readonly retiredCount = input.required<number>();
  readonly loading = input.required<boolean>();
  readonly viewChange = output<'list' | 'map'>();
  readonly add = output<void>();
  readonly purge = output<void>();
  readonly reload = output<void>();
}
