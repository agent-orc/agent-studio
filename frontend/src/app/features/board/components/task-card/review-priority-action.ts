import type { TaskInfo } from '../../../../models/task.model';
import type { TaskService } from '../../../../services/task.service';
import type { NotificationService } from '../../../../services/notification.service';
import { REVIEW_FIRST_ID, REVIEW_NORMAL_ID } from './task-card-view-model';

export function handleReviewPriorityAction(
  id: string, task: TaskInfo, blocked: boolean, jobs: TaskService, notifications: NotificationService,
): boolean {
  if (id !== REVIEW_FIRST_ID && id !== REVIEW_NORMAL_ID) return false;
  if (blocked) return true;
  const priority = id === REVIEW_FIRST_ID ? 'urgent' : 'normal';
  jobs.setReviewPriority(task.id, priority, task.watchPath).subscribe({
    next: () => {
      notifications.success(priority === 'urgent' ? 'Review moved to the front' : 'Review returned to normal order');
      jobs.refresh(true);
    },
    error: () => notifications.error('Could not change review priority'),
  });
  return true;
}
