import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { laneName } from '../../../../models/lane-presentation';
import {
  anomalyLabel, daysUntil, statePresentation, userStatePresentation,
  type SecurityCard, type SecurityPosture,
} from '../operations-security/operations-security.model';

/**
 * The facts behind the ranked security entries (AGT-3575): exposure,
 * credentials, second factor coverage, versions, audit anomalies, and open
 * security cards. Read-only; every value is metadata the API already holds.
 */
@Component({
  selector: 'app-operations-security-facts', standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './operations-security-facts.html', styleUrl: './operations-security-facts.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsSecurityFactsComponent {
  readonly posture = input.required<SecurityPosture>();
  readonly openCard = output<SecurityCard>();
  readonly now = computed(() => Date.parse(this.posture().generatedAt));
  readonly hostCredentials = computed(() => this.posture().credentials.inventory);
  readonly state = statePresentation;
  readonly userState = userStatePresentation;
  readonly anomaly = anomalyLabel;
  readonly lane = laneName;

  /** "in 3 days", "today", "2 days ago", or "No expiry". */
  expiry(iso: string | null): string {
    const days = daysUntil(iso, this.now());
    if (days === null) return 'No expiry';
    if (days === 0) return 'today';
    return days > 0 ? `in ${days} day${days === 1 ? '' : 's'}` : `${-days} day${days === -1 ? '' : 's'} ago`;
  }

  projects(projects: readonly string[]): string {
    return projects.includes('*') ? 'All projects' : projects.join(', ') || 'None';
  }

  sourceNote(source: string, problem: string | null): string | null {
    if (source === 'unavailable') return problem ?? 'This source did not answer.';
    return source === 'task-server' ? 'Read from the Task Server.' : null;
  }

  integrityLabel(state: string): string {
    return state === 'verified' ? 'Verified' : state === 'failed' ? 'Failed' : 'Not verified yet';
  }
}
