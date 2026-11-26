import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { HostAliasService } from '../identity-chip/host-alias.service';

/** Host name with a rename control; shows a generated alias as such (AGT-4034). */
@Component({
  selector: 'app-host-name-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './host-name-editor.component.html',
  styleUrl: './host-name-editor.component.scss',
})
export class HostNameEditorComponent {
  readonly hostId = input.required<string>();
  readonly name = input<string | null>(null);
  readonly role = input<string | null>(null);
  private readonly aliases = inject(HostAliasService);
  readonly renaming = signal(false);
  readonly shown = computed(() => this.aliases.display(this.hostId(), this.name(), this.role()));

  save(value: string): void {
    this.aliases.rename(this.hostId(), value);
    this.renaming.set(false);
  }
}
