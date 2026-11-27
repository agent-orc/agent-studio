import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, timer } from 'rxjs';

interface CapacityFact {
  project: string | null;
  totalSlots: number;
  usedSlots: number;
  freeSlots: number;
  ready: number;
  claimableNow: number;
  dependencyWidth: number;
  cause: string;
}
interface CapacityResponse { fleet: CapacityFact; projects: CapacityFact[] }

@Component({
  selector: 'app-capacity-line',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './capacity-line.component.html',
  styleUrl: './capacity-line.component.scss',
})
export class CapacityLineComponent {
  readonly project = input<string | null>(null);
  private readonly http = inject(HttpClient);
  private readonly response = toSignal(timer(0, 30_000).pipe(
    switchMap(() => this.http.get<CapacityResponse>('/api/operator/coding-capacity').pipe(catchError(() => of(null)))),
  ));
  readonly fact = computed(() => {
    const response = this.response();
    return this.project() ? response?.projects.find(row => row.project === this.project()) : response?.fleet;
  });
  readonly line = computed(() => {
    const fact = this.fact();
    if (!fact) return '';
    const limit = fact.dependencyWidth < fact.ready
      ? `, limited by ${fact.dependencyWidth} chains` : '';
    return `${fact.usedSlots} of ${fact.totalSlots} slots, ${fact.claimableNow} claimable${limit}`;
  });
}
