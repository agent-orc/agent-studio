import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { Subscription, take } from 'rxjs';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { OperationsCardLookupService, type OperationsCardTarget } from '../../state/operations-card-lookup.service';
import { OperationsSecurityFactsComponent } from '../operations-security-facts/operations-security-facts';
import {
  SEVERITY_ORDER, areaLabel, severityPresentation, type SecurityCard, type SecurityPosture, type SecuritySeverity,
} from './operations-security.model';

export const SECURITY_POSTURE_URL = '/api/security/posture';

/**
 * Installation security view on the Operations page (AGT-3575): ranked
 * findings with the ones that need action within 14 days first, then the
 * facts they come from. Reads the same route operator agents use; a caller
 * without security.posture.read gets 403 and the section stays hidden.
 */
@Component({
  selector: 'app-operations-security', standalone: true,
  imports: [DatePipe, LoadingSurfaceComponent, StatusIndicatorComponent, OperationsSecurityFactsComponent],
  templateUrl: './operations-security.html', styleUrl: './operations-security.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsSecurityComponent {
  private readonly http = inject(HttpClient);
  private readonly cards = inject(OperationsCardLookupService);
  private generation = 0;
  private cardRead = new Subscription();
  readonly openTask = output<OperationsCardTarget>();
  readonly posture = signal<SecurityPosture | null>(null);
  readonly error = signal<string | null>(null);
  /** No permission or no route (public demo): the section renders nothing. */
  readonly hidden = signal(false);
  readonly actionSoon = computed(() => this.posture()?.findings.filter(finding => finding.actionSoon) ?? []);
  readonly bands = computed(() => {
    const summary = this.posture()?.summary;
    return summary ? SEVERITY_ORDER.map(severity => ({ severity, count: summary[severity as SecuritySeverity] })) : [];
  });
  readonly severity = severityPresentation;
  readonly areaLabel = areaLabel;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cardRead.unsubscribe());
    this.load();
  }

  load(): void {
    const generation = ++this.generation;
    this.error.set(null);
    this.http.get<SecurityPosture>(SECURITY_POSTURE_URL).pipe(take(1)).subscribe({
      next: value => { if (generation === this.generation) { this.posture.set(value); this.hidden.set(false); } },
      error: (failure: HttpErrorResponse) => {
        if (generation !== this.generation) return;
        if (failure.status === 401 || failure.status === 403 || failure.status === 404) { this.hidden.set(true); return; }
        this.error.set('The security view could not be loaded.');
      },
    });
  }

  /** A later card click replaces a lookup that has not answered yet. */
  openCard(card: SecurityCard): void {
    this.cardRead.unsubscribe();
    this.cardRead = this.cards.resolve(card.project, card.key).subscribe(target => this.openTask.emit(target));
  }
}
