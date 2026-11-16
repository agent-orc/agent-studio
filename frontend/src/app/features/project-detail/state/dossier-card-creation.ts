import { Observable, catchError, map, of, switchMap, throwError } from 'rxjs';
import type { CreateTaskRequest } from '../../../models/task.model';
import type { TaskService } from '../../../services/task.service';
import { taskKeyTail } from '../components/workbench-decision-panel/workbench-decision-panel.util';
import type { WorkbenchDecisionDraftCard } from './workbench-decision-draft.store';

/** The task path of a project, or an error naming the project. */
export function projectWatchPath(tasks: TaskService, projectName: string): Observable<string> {
  return tasks.getWatchPaths().pipe(map(entries => {
    const path = entries.find(entry => entry.name === projectName)?.path;
    if (!path) throw new Error(`Could not resolve the task path for ${projectName}.`);
    return path;
  }));
}

/**
 * Creates one card from a Dossier and reads it back. A card whose details
 * cannot be read is removed again, so a retry never leaves a second card.
 */
export function createDossierCard(
  tasks: TaskService, projectName: string, request: Omit<CreateTaskRequest, 'watchPath'>,
): Observable<WorkbenchDecisionDraftCard> {
  return projectWatchPath(tasks, projectName).pipe(
    switchMap(watchPath => tasks.createJob({ ...request, watchPath }).pipe(
      switchMap(created => tasks.getDetailByProject(created.id, projectName).pipe(
        map(detail => ({ detail, id: created.id, watchPath })),
        catchError(() => tasks.deleteJob(created.id, watchPath).pipe(
          catchError(() => throwError(() => new Error(
            `The task was created, but its details could not be read. The task may remain on the board. Ask a Studio operator to remove task ${created.id}.`))),
          switchMap(() => throwError(() => new Error('The task was created, but its details could not be read. The task was removed. Try again.'))),
        )),
      )))),
    map(({ detail, id, watchPath }) => ({
      id,
      watchPath,
      key: detail.info.key || detail.info.displayKey || taskKeyTail(detail.info.taskKey),
      taskKey: detail.info.taskKey,
      title: detail.info.title,
      lane: detail.info.state,
    })),
  );
}

/** Resolve an older draft's missing id; removal must never use its display key. */
export function resolveDossierCardId(
  tasks: TaskService, projectName: string, card: WorkbenchDecisionDraftCard,
): Observable<WorkbenchDecisionDraftCard> {
  if (card.id) return of(card);
  return tasks.getDetailByProject(card.taskKey || card.key, projectName).pipe(
    map(detail => detail.info.id ? { ...card, id: detail.info.id, watchPath: detail.info.watchPath } : card),
    catchError(() => of(card)),
  );
}
