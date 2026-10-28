import { Injectable, inject } from '@angular/core';
import { EMPTY, catchError, filter, map, of, take, type Observable } from 'rxjs';
import { TaskService } from '../../../services/task.service';

export interface OperationsCardTarget { jobId: string; watchPath: string }

/** Resolves a marker's exact task identity, including cards in the archive. */
@Injectable({ providedIn: 'root' })
export class OperationsCardLookupService {
  private readonly tasks = inject(TaskService);

  resolve(project: string, key: string): Observable<OperationsCardTarget> {
    const sameKey = (value: string | null | undefined) => value?.toLowerCase() === key.toLowerCase();
    const live = this.tasks.jobs().find(item => item.projectName === project && (sameKey(item.key) || sameKey(item.id)));
    if (live) return of({ jobId: live.id, watchPath: live.watchPath });
    return this.tasks.getCore(key, project).pipe(
      take(1),
      map(response => response.body),
      filter(core => !!core && core.state !== 'warming' && core.projectName === project
        && (sameKey(core.key) || sameKey(core.taskKey) || sameKey(core.id))),
      map(core => ({ jobId: core!.id, watchPath: core!.watchPath })),
      catchError(() => EMPTY),
    );
  }
}
