import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { PromoteConceptResponse, PromotedConceptTask } from '../../models/task.model';
import { TaskService } from '../../services/task.service';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';
import {
  cardLabel,
  promotionErrorMessage,
  promotionSlices,
} from './concept-promotion.util';

/**
 * Create implementation cards from a concept Dossier (AGT-3701). Lists every
 * implementation slice with its order, title, and working decision, and lets
 * the owner create all or some of them. Promotion never moves the concept
 * card: accepting the concept stays a separate lane move.
 *
 * Used by the Dossier view (keyed by the Dossier's source concept card) and
 * by the concept card's Overview.
 */
@Component({
  selector: 'app-concept-promotion',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './concept-promotion.component.html',
  styleUrl: './concept-promotion.component.scss',
})
export class ConceptPromotionComponent {
  private readonly tasks = inject(TaskService);

  /** Concept card id or key. */
  readonly taskRef = input.required<string>();
  readonly projectName = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly promoted = output<PromotedConceptTask[]>();

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly creating = signal(false);
  readonly plan = signal<PromoteConceptResponse | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly createError = signal<string | null>(null);
  readonly created = signal<PromotedConceptTask[]>([]);
  readonly lastCreated = signal<PromotedConceptTask[]>([]);
  readonly selected = signal<ReadonlySet<number>>(new Set());

  readonly slices = computed(() => {
    const plan = this.plan();
    return plan ? promotionSlices(plan, this.created()) : [];
  });
  readonly remaining = computed(() => this.slices().filter(slice => !slice.card));
  readonly selectedCount = computed(() =>
    this.remaining().filter(slice => this.selected().has(slice.index)).length);
  readonly allSelected = computed(() =>
    this.remaining().length > 0 && this.selectedCount() === this.remaining().length);
  readonly createLabel = computed(() => {
    const count = this.selectedCount();
    if (count === 0) return 'Create cards';
    return count === this.remaining().length && count > 1
      ? `Create all ${count} cards`
      : `Create ${count} card${count === 1 ? '' : 's'}`;
  });
  readonly cardLabel = cardLabel;

  private readonly reset = effect(() => {
    void this.taskRef();
    this.open.set(false);
    this.plan.set(null);
    this.created.set([]);
    this.lastCreated.set([]);
    this.loadError.set(null);
    this.createError.set(null);
  });

  openPanel(): void {
    this.open.set(true);
    this.lastCreated.set([]);
    this.load();
  }

  close(event?: Event): void {
    event?.preventDefault();
    if (this.creating()) return;
    this.open.set(false);
    this.createError.set(null);
  }

  toggle(index: number): void {
    const next = new Set(this.selected());
    if (next.has(index)) next.delete(index);
    else next.add(index);
    this.selected.set(next);
  }

  toggleAll(): void {
    this.selected.set(this.allSelected()
      ? new Set()
      : new Set(this.remaining().map(slice => slice.index)));
  }

  create(): void {
    const indexes = this.remaining()
      .filter(slice => this.selected().has(slice.index))
      .map(slice => slice.index);
    if (indexes.length === 0 || this.creating()) return;
    this.creating.set(true);
    this.createError.set(null);
    this.tasks.promoteConcept(
      this.taskRef(), indexes, this.watchPath() || undefined, this.projectName() || undefined,
    ).subscribe({
      next: result => {
        this.creating.set(false);
        this.created.set([...this.created(), ...result.created]);
        this.lastCreated.set(result.created);
        this.selected.set(new Set());
        this.tasks.refresh(true);
        this.promoted.emit(result.created);
      },
      error: error => {
        this.creating.set(false);
        this.createError.set(promotionErrorMessage(error, 'Could not create the implementation cards.'));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.tasks.getPromoteConcept(
      this.taskRef(), this.watchPath() || undefined, this.projectName() || undefined,
    ).subscribe({
      next: plan => {
        this.loading.set(false);
        this.plan.set(plan);
        this.created.set([]);
        const slices = promotionSlices(plan);
        this.selected.set(new Set(slices.filter(slice => !slice.card).map(slice => slice.index)));
      },
      error: error => {
        this.loading.set(false);
        this.loadError.set(promotionErrorMessage(error, 'Could not read the Dossier implementation slices.'));
      },
    });
  }
}
