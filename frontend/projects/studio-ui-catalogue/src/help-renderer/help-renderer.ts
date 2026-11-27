import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';

@Component({
  selector: 'app-catalogue-help-renderer',
  imports: [MarkdownViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './help-renderer.html',
})
export class HelpRendererComponent {
  readonly source = input.required<string>();
}
