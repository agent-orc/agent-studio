import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { WikiAgentReads } from '../../../../../models/project-docs.model';

@Component({
  selector: 'app-wiki-agent-reads',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-agent-reads.component.html',
  styleUrl: './wiki-agent-reads.component.scss',
  host: { 'data-testid': 'project-wiki-agent-reads-panel' },
})
export class WikiAgentReadsComponent {
  readonly reads = input.required<WikiAgentReads>();

  formatTimestamp(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('en-US');
  }
}
