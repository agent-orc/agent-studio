import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService } from '../../../../services/quality-studio.service';

@Component({
  selector: 'app-quality-studio-explorer-row',
  standalone: true,
  imports: [TreeRowComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quality-studio-explorer-row.component.html',
})
export class QualityStudioExplorerRowComponent {
  readonly projectName = input.required<string>();
  readonly displayLabel = input.required<string>();
  readonly quality = inject(QualityStudioService);
  private readonly auth = inject(AuthSessionState);
  readonly show = computed(() => this.auth.status()?.user?.role === 'owner'
    || this.quality.status(this.projectName())?.configured === true);
  readonly ready = computed(() => this.quality.status(this.projectName())?.state === 'ready');

  constructor() { effect(() => this.quality.ensure(this.projectName())); }

  open(event?: Event): void {
    event?.stopPropagation();
    if (this.ready()) this.quality.open(this.projectName());
  }
}
