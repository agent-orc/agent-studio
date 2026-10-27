import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Subject, auditTime, catchError, filter, forkJoin, interval, map, merge, of, switchMap } from 'rxjs';
import type { DeliveryColumnsResponse } from '../../../models/delivery-column.model';
import { TaskService } from '../../../services/task.service';

/** Same fallback cadence as the board and the card environment badges. */
export const DELIVERY_POLL_MS = 30_000;

/**
 * Live source of the board's Delivery section: the `deploy/lanes` read model
 * of every project the board shows. It refreshes when the watched projects
 * change, on task events (a card reaching Accepted or Archive), and on the
 * 30-second deploy poll, because a deploy run changes no task.
 */
@Injectable({ providedIn: 'root' })
export class DeliveryColumnsService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly refresh$ = new Subject<void>();
  private projects: readonly string[] = [];

  readonly responses = signal<readonly DeliveryColumnsResponse[]>([]);
  readonly error = signal(false);

  constructor() {
    merge(this.refresh$, interval(DELIVERY_POLL_MS), (this.tasks.taskEvents ?? EMPTY).pipe(auditTime(1_000))).pipe(
      filter(() => this.projects.length > 0),
      switchMap(() => {
        const names = this.projects;
        return forkJoin(names.map((name) => this.http.get<DeliveryColumnsResponse>(
          `/api/projects/${encodeURIComponent(name)}/deploy/lanes`,
        ).pipe(catchError(() => of(null))))).pipe(map((rows) => ({ names, rows })));
      }),
      takeUntilDestroyed(),
    ).subscribe(({ names, rows }) => {
      if (names !== this.projects) return;
      const loaded = rows.filter((row): row is DeliveryColumnsResponse => row !== null);
      this.responses.set(loaded);
      this.error.set(loaded.length !== names.length);
    });
  }

  /** Watch these projects; an empty list stops polling and clears the section. */
  watch(projects: readonly string[]): void {
    if (projects.length === this.projects.length && projects.every((name, i) => name === this.projects[i])) return;
    this.projects = [...projects];
    if (this.projects.length === 0) {
      this.responses.set([]);
      this.error.set(false);
      return;
    }
    this.responses.update((rows) => rows.filter((row) => this.projects.includes(row.projectId)));
    this.refresh$.next();
  }
}
