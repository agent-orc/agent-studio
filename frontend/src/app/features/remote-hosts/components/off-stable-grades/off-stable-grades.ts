import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { laneName } from '../../../../models/lane-presentation';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

/** Mirrors backend `OffStableGradedCard`. */
export interface OffStableGradedCard {
  taskKey: string;
  id: string;
  projectName: string;
  title: string;
  lane: string;
  attemptId: string;
  gradedAt: string | null;
  workerReleaseId: string | null;
  /** The grade report's `workerReleaseStable` sentence (AGT-3689). */
  notice: string;
}

/**
 * AGT-3845: the cards whose latest Remote Review grade came from a review role
 * that did not run the Stable build, with a link to each card. Each card's
 * detail offers a fresh review on the current build. Absent when there is none.
 */
@Component({
  selector: 'app-off-stable-grades',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './off-stable-grades.html',
  styleUrl: './off-stable-grades.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OffStableGradesComponent {
  private readonly http = inject(HttpClient);
  private readonly navigation = inject(TaskReferenceNavigationService);
  readonly cards = signal<readonly OffStableGradedCard[]>([]);
  readonly laneName = laneName;

  constructor() {
    this.http.get<OffStableGradedCard[]>('/api/v1/management/host-releases/off-stable-grades').subscribe({
      next: cards => this.cards.set(cards ?? []),
      // A failed read leaves the list out rather than claiming there is none.
      error: () => this.cards.set([]),
    });
  }

  openCard(event: MouseEvent, taskKey: string): void {
    event.preventDefault();
    this.navigation.openReferenceOrNotify(taskKey);
  }
}
