import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChipComponent } from '../../../../components/chip/chip.component';
import { DataGridComponent, GridCellDirective } from '../../../../components/data-grid';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import type { MenuItem } from '../../../../components/menu';
import { OverflowMenuComponent } from '../../../../components/overflow-menu/overflow-menu.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { formatAccessTime, type EnrolledRunner, type RunnerEnrollment } from '../../models/access.model';
import {
  accessColumns, enrollmentIdentity, enrollmentOpen, enrollmentStatus, enrollmentTone, identityLabel, projectNames, runnerExpiry, runnerIdentity,
  type EnrollmentStatus, type RunnerExpiry,
} from '../../models/access-view.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';

export interface RunnerRow {
  id: string;
  name: string;
  sub: string;
  runner: EnrolledRunner;
  identity: string;
  projects: string;
  revoked: boolean;
  expiry: RunnerExpiry;
  menu: readonly MenuItem[];
}

export interface EnrollmentRow {
  id: string;
  name: string;
  sub: string;
  enrollment: RunnerEnrollment;
  identity: string;
  projects: string;
  status: EnrollmentStatus;
  tone: StatusTone;
  expires: string;
  menu: readonly MenuItem[];
}

const OPEN: MenuItem = { kind: 'row', id: 'open', label: 'Open details' };

/**
 * Enrolled runners and the one-time codes that enroll them (AGT-3186).
 * Runners and the engine authenticate with their enrollment credentials and
 * Runner scopes, not with role bindings: the access column shows the identity
 * and the projects it is bound to (SM-9). A run host (F4) holds only
 * runner.deploy.
 */
@Component({
  selector: 'app-access-runners',
  standalone: true,
  imports: [ChipComponent, DataGridComponent, EmptyStateComponent, GridCellDirective, OverflowMenuComponent, PageSectionComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-runners.component.html',
  styleUrl: './access-runners.component.scss',
})
export class AccessRunnersComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly now = inject(ACCESS_CLOCK);
  readonly runnerColumns = accessColumns('Runner', 'Identity and projects', 'Credential');
  readonly enrollmentColumns = accessColumns('Code for', 'Identity and projects', 'Expires');

  readonly runners = computed<RunnerRow[]>(() => this.access.runners().map(runner => {
    const identity = runnerIdentity(runner);
    const revoked = !!runner.revokedAt;
    return {
      id: runner.id, name: runner.name, sub: runner.id, runner, revoked, identity: identityLabel(identity),
      projects: identity === 'orchestrator' ? 'Not project-bound' : projectNames(runner.projects, this.access.projects()),
      expiry: runnerExpiry(runner, this.now()),
      menu: revoked ? [OPEN] : [
        OPEN,
        ...(identity === 'orchestrator' ? [] : [{ kind: 'row', id: 'edit', label: 'Edit projects' } as MenuItem]),
        { kind: 'separator' }, { kind: 'row', id: 'revoke', label: 'Revoke…', danger: true },
      ],
    };
  }));

  readonly enrollments = computed<EnrollmentRow[]>(() => this.access.enrollments().map(enrollment => {
    const identity = enrollmentIdentity(enrollment);
    const status = enrollmentStatus(enrollment, this.now());
    return {
      id: enrollment.id, name: enrollment.name, sub: '', enrollment, status, tone: enrollmentTone(status), identity: identityLabel(identity),
      projects: identity === 'orchestrator' ? 'Not project-bound' : enrollment.projects ? projectNames(enrollment.projects, this.access.projects()) : '',
      expires: formatAccessTime(enrollment.expiresAt),
      menu: enrollmentOpen(enrollment, this.now())
        ? [OPEN, { kind: 'separator' }, { kind: 'row', id: 'revoke', label: 'Revoke…', danger: true }] : [OPEN],
    };
  }));

  readonly runnerId = (row: RunnerRow) => row.id;
  readonly runnerLabel = (row: RunnerRow) => row.name;
  readonly runnerTestId = (row: RunnerRow) => `access-runner-${row.id}`;
  readonly runnerMuted = (row: RunnerRow) => row.revoked;
  readonly enrollmentId = (row: EnrollmentRow) => row.id;
  readonly enrollmentLabel = (row: EnrollmentRow) => `the code for ${row.name}`;
  readonly enrollmentTestId = (row: EnrollmentRow) => `access-enrollment-${row.id}`;
  readonly enrollmentMuted = (row: EnrollmentRow) => row.status === 'Expired or revoked';

  actRunner(action: string, row: RunnerRow): void {
    this.selection.open('runner', row.id, action === 'revoke' ? 'revoke' : null);
  }

  actEnrollment(action: string, row: EnrollmentRow): void {
    this.selection.open('enrollment', row.id, action === 'revoke' ? 'revoke' : null);
  }
}
