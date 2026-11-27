import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NotificationComponent } from '@studio/ui/components/notification/notification.component';
import { BoardFiltersService } from '../../../board';

const DISPLAYED_TASK_LIMIT = 10;

interface AcceptedIntegrationAlertItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  acceptedAt: string;
  integrationStatus: string;
  lastOutcome?: string | null;
  detail?: string | null;
}

interface AcceptedIntegrationAlertSnapshot {
  active: boolean;
  stalledTaskCount: number;
  thresholdMinutes: number;
  oldestAcceptedAt: string | null;
  observedAt: string;
  items: AcceptedIntegrationAlertItem[];
}

@Component({
  selector: 'app-accepted-integration-alert-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './accepted-integration-alert-banner.html',
  styleUrl: './accepted-integration-alert-banner.scss',
})
export class AcceptedIntegrationAlertBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly boardFilters = inject(BoardFiltersService);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly projects = input<readonly string[]>([]);
  readonly snapshot = signal<AcceptedIntegrationAlertSnapshot | null>(null);
  readonly visibleItems = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot?.active) return [];
    const projects = this.projects();
    if (projects.length === 0) return snapshot.items;
    const visible = new Set(projects.map(project => project.toLowerCase()));
    return snapshot.items.filter(item => visible.has(item.projectName.toLowerCase()));
  });
  readonly thresholdMinutes = computed(() => this.snapshot()?.thresholdMinutes ?? 30);
  readonly displayedTaskKeys = computed(() => this.visibleItems()
    .slice(0, DISPLAYED_TASK_LIMIT)
    .map(item => item.taskKey)
    .join(', '));
  readonly remainingTaskCount = computed(() => Math.max(
    0,
    this.visibleItems().length - DISPLAYED_TASK_LIMIT,
  ));
  readonly fullListHref = computed(() => {
    const projects = [...new Set(this.visibleItems().map(item => item.projectName))];
    const filters = ['integration:stalled'];
    if (projects.length > 0) filters.push(`projects:${projects.join(',')}`);
    return `#/board&filters=${encodeURIComponent(filters.join(';'))}`;
  });

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private refresh(): void {
    this.http.get<AcceptedIntegrationAlertSnapshot>('/api/pipeline/accepted-integration-alert').subscribe({
      next: snapshot => {
        this.snapshot.set(snapshot);
        this.boardFilters.updateAcceptedIntegrationAlertItems(snapshot.active ? snapshot.items : []);
      },
      error: () => {
        this.snapshot.set(null);
        this.boardFilters.updateAcceptedIntegrationAlertItems([]);
      },
    });
  }
}
