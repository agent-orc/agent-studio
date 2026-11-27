import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, signal } from '@angular/core';

export type LoadingSurfaceKind = 'board' | 'task-detail' | 'list';

/** Skeleton feedback that stays hidden for fast (<200 ms) loads. */
@Component({
  selector: 'sui-loading-surface, app-loading-surface',
  standalone: true,
  templateUrl: './loading-surface.component.html',
  styleUrl: './loading-surface.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoadingSurfaceComponent {
  readonly kind = input<LoadingSurfaceKind>('list');
  readonly label = input('Loading…');
  readonly visible = signal(false);
  readonly contextual = signal(false);

  constructor() {
    const destroyRef = inject(DestroyRef);
    const showTimer = setTimeout(() => this.visible.set(true), 200);
    const contextTimer = setTimeout(() => this.contextual.set(true), 1000);
    destroyRef.onDestroy(() => {
      clearTimeout(showTimer);
      clearTimeout(contextTimer);
    });
  }
}
