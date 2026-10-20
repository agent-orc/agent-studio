import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-environment-header', standalone: true,
  templateUrl: './environment-header.html', styleUrl: './environment-header.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHeaderComponent {
  readonly view = input.required<'list' | 'map'>();
  readonly retiredCount = input.required<number>();
  readonly loading = input.required<boolean>();
  readonly viewChange = output<'list' | 'map'>();
  readonly add = output<void>();
  readonly purge = output<void>();
  readonly reload = output<void>();
}
