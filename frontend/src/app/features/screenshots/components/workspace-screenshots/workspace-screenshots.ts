import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { TaskService } from '../../../../services/task.service';
import type { TaskScreenshot } from '../../../../features/screenshots';
import { ScreenshotStripComponent } from '../screenshot-strip/screenshot-strip.component';

interface HourBucket {
  bucketKey: string;
  bucketLabel: string;
  items: TaskScreenshot[];
}

const STORAGE_WINDOW_KEY = 'workspaceScreenshots.windowHours';
const STORAGE_PROJECT_KEY = 'workspaceScreenshots.project';

const WINDOW_OPTIONS: { hours: number; label: string; testId: string }[] = [
  { hours: 24, label: '24 h', testId: '24h' },
  { hours: 72, label: '3 days', testId: '3d' },
  { hours: 168, label: '7 days', testId: '7d' },
];

/**
 * Workspace-wide visual evidence reel. Folds every recent screenshot
 * across all watched projects and groups them into hour buckets so a
 * burst of activity stays visually together. Each thumbnail opens the
 * shared lightbox; the lightbox's "Open task" link navigates to the
 * originating job page so the reel is a discovery surface, not a
 * dead-end gallery.
 *
 * The endpoint behind this is `GET /api/workspace/screenshots`.
 * Project filter and window selection live in localStorage so the
 * user's last view is preserved across sessions.
 */
@Component({
  selector: 'app-workspace-screenshots',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SegmentedControlComponent, ScreenshotStripComponent],
  templateUrl: './workspace-screenshots.html',
  styleUrl: './workspace-screenshots.scss',
})
export class WorkspaceScreenshotsComponent implements OnInit, OnDestroy {
  private readonly jobs = inject(TaskService);

  readonly projectName = input<string | null>(null);
  readonly openTask = output<TaskScreenshot>();

  readonly windowOptions = WINDOW_OPTIONS;
  readonly segmentedWindowOptions = this.windowOptions.map(opt => ({ value: opt.hours, label: opt.label, testid: 'wss-win-' + opt.testId }));

  readonly windowHours = signal<number>(this.loadWindow());
  readonly projectFilter = signal<string | null>(this.loadProject());
  readonly entries = signal<TaskScreenshot[]>([]);
  readonly loading = signal<boolean>(false);
  readonly loaded = signal<boolean>(false);

  readonly projects = computed(() => {
    const set = new Set<string>();
    for (const e of this.entries()) {
      if (e.projectName) set.add(e.projectName);
    }
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  });

  readonly projectCount = computed(() => this.projects().length);
  readonly effectiveProjectFilter = computed(() => this.projectName() ?? this.projectFilter());

  readonly buckets = computed<HourBucket[]>(() => {
    const items = this.entries();
    if (items.length === 0) return [];
    const byKey = new Map<string, HourBucket>();
    for (const it of items) {
      const d = new Date(it.timestampUtc);
      const key = bucketKey(d);
      const label = bucketLabel(d);
      let b = byKey.get(key);
      if (!b) {
        b = { bucketKey: key, bucketLabel: label, items: [] };
        byKey.set(key, b);
      }
      b.items.push(it);
    }
    // Sort buckets newest-first; entries inside each bucket are already newest-first.
    return Array.from(byKey.values()).sort((a, b) => b.bucketKey.localeCompare(a.bucketKey));
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  setWindow(hours: number): void {
    if (this.windowHours() === hours) return;
    this.windowHours.set(hours);
    try {
      localStorage.setItem(STORAGE_WINDOW_KEY, String(hours));
    } catch {
      /* ignore */
    }
    this.refresh();
  }

  setProject(value: string): void {
    if (this.projectName()) return;
    const v = value && value.length > 0 ? value : null;
    this.projectFilter.set(v);
    try {
      if (v) localStorage.setItem(STORAGE_PROJECT_KEY, v);
      else localStorage.removeItem(STORAGE_PROJECT_KEY);
    } catch {
      /* ignore */
    }
    this.refresh();
  }

  refresh(silent = false): void {
    if (!silent) this.loading.set(true);
    this.jobs.getWorkspaceScreenshots(this.windowHours(), this.effectiveProjectFilter()).subscribe({
      next: (res) => {
        this.entries.set(res?.screenshots ?? []);
        this.loaded.set(true);
      },
      error: () => {
        /* keep prior entries */
      },
      complete: () => this.loading.set(false),
    });
  }

  onOpenTask(s: TaskScreenshot): void {
    this.openTask.emit(s);
  }

  private loadWindow(): number {
    try {
      const raw = localStorage.getItem(STORAGE_WINDOW_KEY);
      const n = raw ? Number(raw) : 72;
      return WINDOW_OPTIONS.some((o) => o.hours === n) ? n : 72;
    } catch {
      return 72;
    }
  }

  private loadProject(): string | null {
    try {
      const raw = localStorage.getItem(STORAGE_PROJECT_KEY);
      return raw && raw.length > 0 ? raw : null;
    } catch {
      return null;
    }
  }
}

function bucketKey(d: Date): string {
  // Local-hour bucket key. Newest-first sort uses string compare so the
  // key shape must be lexicographically chronological.
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:00`;
}

function bucketLabel(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:00`;
}
