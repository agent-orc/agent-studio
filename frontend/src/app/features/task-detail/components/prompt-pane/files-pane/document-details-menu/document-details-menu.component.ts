import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskArtifact } from '../../../../../../models/task.model';
import { NowTickService } from '../../../../../../services/now-tick.service';
import { formatRelativeTime } from '../../../../../../services/format.util';
import { generatedFileProvenance } from '../../../generated-file-provenance.util';

@Component({
  selector: 'app-document-details-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './document-details-menu.component.html',
  styleUrl: './document-details-menu.component.scss',
})
export class DocumentDetailsMenuComponent {
  private readonly nowTick = inject(NowTickService);

  readonly file = input.required<TaskArtifact>();
  readonly title = input.required<string>();
  readonly sourceVisible = input(false);
  readonly historyVisible = input(false);
  readonly toggleSource = output<void>();
  readonly toggleHistory = output<void>();

  provenance() {
    return generatedFileProvenance(this.file().generation);
  }

  formattedSize(): string {
    const bytes = this.file().sizeBytes;
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 102.4) / 10} KB`;
    return `${Math.round(bytes / (1024 * 102.4)) / 10} MB`;
  }

  formattedAge(): string {
    return formatRelativeTime(this.file().mtime, this.nowTick.now());
  }

  formattedTokens(): string {
    const generation = this.file().generation;
    if (!generation || generation.tokensTotal <= 0) return 'Not recorded';
    return `${generation.tokensIn.toLocaleString('en-US')} in · ${generation.tokensOut.toLocaleString('en-US')} out · ${generation.tokensTotal.toLocaleString('en-US')} total`;
  }
}
