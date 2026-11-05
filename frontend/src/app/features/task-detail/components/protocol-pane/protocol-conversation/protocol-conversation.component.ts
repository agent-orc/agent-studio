import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { ConversationEvent, RawLineRange } from 'coding-agent-chat/core';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import { ActivityEventPresentationDirective } from '../activity-event-presentation.directive';
import { ConversationScrollFocusDirective } from '../../../../../directives/conversation-scroll-focus.directive';

/** Loads the conversation renderer only when the Activity view needs it. */
@Component({
  selector: 'app-protocol-conversation',
  standalone: true,
  imports: [ConversationViewComponent, ConversationScrollFocusDirective, ActivityEventPresentationDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './protocol-conversation.component.html',
})
export class ProtocolConversationComponent {
  readonly events = input.required<readonly ConversationEvent[]>();
  readonly isRunning = input(false);
  readonly queuedFollowUp = input(false);
  readonly openTrace = output<RawLineRange | null>();
  readonly openFollowUp = output<string>();
  readonly openVerboseDebug = output<void>();
}
