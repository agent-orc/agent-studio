import { LoadingSurfaceComponent } from '../../components/async-feedback';
import { StatusIndicatorComponent } from '../../components/status-indicator/status-indicator.component';
import { EMPTY, catchError, switchMap, timer } from 'rxjs';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, effect, inject, input, signal } from '@angular/core';
import { StudioReleasesService, type StudioReleasesView } from './services/studio-releases.service';

@Component({
  selector: 'app-studio-releases', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  host: { tabindex: '0', '(keydown)': 'scrollOnKey($event)' },
  imports: [DatePipe, StatusIndicatorComponent, LoadingSurfaceComponent], templateUrl: './studio-releases.component.html', styleUrl: './studio-releases.component.scss',
})
export class StudioReleasesComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly releases = inject(StudioReleasesService);
  readonly project = input.required<string>();
  readonly view = signal<StudioReleasesView | null>(null);
  readonly error = signal('');
  readonly loading = signal(true);
  constructor() {
    afterNextRender(() => this.element.nativeElement.focus());
    effect(onCleanup => {
      const project = this.project();
      this.view.set(null); this.error.set(''); this.loading.set(true);
      const subscription = timer(0, 30000).pipe(
        switchMap(() => this.releases.view(project).pipe(catchError(error => {
          this.view.set(null);
          this.error.set(error.status === 404 ? 'Releases are available for the Studio source project.'
            : 'Release records could not be loaded. Retrying in 30 seconds.');
          this.loading.set(false);
          return EMPTY;
        }))),
      ).subscribe(view => { this.view.set(view); this.error.set(''); this.loading.set(false); });
      onCleanup(() => subscription.unsubscribe());
    });
  }
  scrollOnKey(event: KeyboardEvent): void {
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const target = event.target as HTMLElement;
    if (target.closest('input, textarea, select, [contenteditable="true"], [role="combobox"]')) return;
    const host = this.element.nativeElement;
    const steps: Record<string, number> = {
      ArrowDown: 48, ArrowUp: -48, PageDown: host.clientHeight * .9, PageUp: -host.clientHeight * .9,
      Home: -host.scrollHeight, End: host.scrollHeight,
    };
    const step = steps[event.key];
    if (step === undefined) return;
    host.scrollTop += step;
    event.preventDefault(); event.stopPropagation();
  }
  waiting(since: string | null): string {
    if (!since) return 'Integration time unavailable';
    const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(since)) / 60000));
    if (minutes < 60) return `Waiting ${minutes} min`;
    if (minutes < 1440) return `Waiting ${Math.floor(minutes / 60)} h ${minutes % 60} min`;
    return `Waiting ${Math.floor(minutes / 1440)} d ${Math.floor(minutes % 1440 / 60)} h`;
  }
}
