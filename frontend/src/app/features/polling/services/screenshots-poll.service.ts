import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { TaskScreenshot, TaskScreenshotsResponse } from '../../../features/screenshots';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';

/**
 * Polls the per-job screenshot listing (`/api/tasks/{id}/screenshots`)
 * on a slow cadence (10 s). Screenshots only appear when a Playwright
 * spec or an agent script writes a new file to `<job>/results/`, so a
 * shorter cadence would burn requests for nothing; the file system is
 * cheap on the backend, so 10 s is a comfortable trade.
 */
@Injectable()
export class ScreenshotsPollService extends TaskBackgroundPoller<TaskScreenshotsResponse | null> {
  private readonly jobService = inject(TaskService);

  protected readonly intervalMs = 10_000;

  readonly screenshots = signal<TaskScreenshot[]>([]);
  readonly loadError = signal(false);

  protected fetch(jobId: string, watchPath: string): Observable<TaskScreenshotsResponse | null> {
    return this.jobService.getJobScreenshots(jobId, watchPath);
  }

  protected applyResponse(res: TaskScreenshotsResponse | null): void {
    this.screenshots.set(res?.screenshots ?? []);
    this.loadError.set(false);
  }

  protected override onError(): void { this.loadError.set(true); }

  protected clearValue(): void {
    this.screenshots.set([]);
    this.loadError.set(false);
  }
}
