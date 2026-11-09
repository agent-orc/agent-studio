import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { TASK_REFERENCE_CHAT_PROVIDER } from '../../../services/task-reference-chat.provider';

/** Adds task-key navigation to a concept doc only while its modal is open. */
@Component({
  selector: 'app-info-button-markdown-body',
  standalone: true,
  imports: [MarkdownViewComponent],
  providers: [TASK_REFERENCE_CHAT_PROVIDER],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './info-button-markdown-body.component.html',
})
export class InfoButtonMarkdownBodyComponent {
  readonly source = input.required<string>();
}
