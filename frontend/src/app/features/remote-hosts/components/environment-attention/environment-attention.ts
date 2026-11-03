import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { EnvironmentCause } from '../../models/environment-attention';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';

@Component({ selector: 'app-environment-attention', standalone: true,
  imports: [DisclosureMarkerComponent],
  templateUrl: './environment-attention.html', styleUrl: './environment-attention.scss',
  changeDetection: ChangeDetectionStrategy.OnPush })
export class EnvironmentAttentionComponent {
  readonly causes = input.required<readonly EnvironmentCause[]>();
  readonly now = input.required<number>();
  readonly reconnect = output<string>();
  readonly expanded = signal(false);
  age(value: string | null): string {
    if (!value) return 'Age unknown';
    const minutes = Math.max(0, Math.floor((this.now() - Date.parse(value)) / 60_000));
    return minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h`;
  }
}
