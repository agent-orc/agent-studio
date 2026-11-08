import type { Provider } from '@angular/core';
import { CHAT_TASK_REFERENCE_PROVIDER } from 'coding-agent-chat/markdown';
import { TaskReferenceNavigationService } from './task-reference-navigation.service';

/** Install task-key links on views that actually render Markdown. */
export const TASK_REFERENCE_CHAT_PROVIDER: Provider = {
  provide: CHAT_TASK_REFERENCE_PROVIDER,
  useExisting: TaskReferenceNavigationService,
};
