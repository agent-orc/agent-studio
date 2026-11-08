import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { InfoButtonMarkdownBodyComponent } from './info-button-markdown-body/info-button-markdown-body.component';
import { DialogComponent } from '../dialog/dialog.component';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';
import { ModalStackService } from '../../services/modal-stack.service';

interface ConceptDocPayload {
  readonly topic: string;
  readonly title: string;
  readonly body: string;
}

/**
 * Subtle "i" trigger that opens a centered modal with the rendered
 * concept doc for the given topic. The doc body is fetched on first open
 * from <c>GET /api/concept-docs/{topic}</c>; the FE never duplicates the
 * prose. Body is rendered through the canonical Markdown view,
 * and the modal shell is the app-wide {@link DialogComponent} so the
 * surface flips light/dark from studio tokens with no per-host colours.
 *
 * Every lane carries one: the trigger is wired from the board lane header
 * (and the studio-shell active panel) so an operator can read what any
 * lane means without leaving the surface.
 */
@Component({
  selector: 'app-info-button',
  standalone: true,
  imports: [TooltipDirective, InfoButtonMarkdownBodyComponent, DialogComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './info-button.component.html',
  styleUrl: './info-button.component.scss'
})
export class InfoButtonComponent {
  private readonly http = inject(HttpClient);

  readonly topic = input.required<string>();
  /** Eyebrow shown above the modal title. Defaults to the lane framing. */
  readonly eyebrow = input<string>('Lane');
  /** Trigger aria-label + tooltip. Defaults to the lane framing. */
  readonly label = input<string>('How does this lane work?');
  /** Header title shown until the doc's own H1 loads (or on error). */
  readonly fallbackTitle = input<string>('About this lane');

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  private readonly doc = signal<ConceptDocPayload | null>(null);

  readonly title = computed(() => this.doc()?.title ?? this.fallbackTitle());
  readonly body = computed<string | null>(() => this.doc()?.body ?? null);

  toggle(event: Event): void {
    event.stopPropagation();
    if (this.open()) {
      this.close();
      return;
    }
    this.open.set(true);
    if (!this.doc() && !this.loading()) {
      void this.load();
    }
  }

  close(): void {
    this.open.set(false);
  }

  // Escape routes through ModalStack so a real modal above wins first.
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;
  private readonly stackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.modalStackDispose) {
      this.modalStackDispose = this.modalStack.push('info-button', () => this.close());
    } else if (!isOpen && this.modalStackDispose) {
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly stackTeardown = this.destroyRef.onDestroy(() => this.modalStackDispose?.());

  private async load(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const payload = await firstValueFrom(
        this.http.get<ConceptDocPayload>(`/api/concept-docs/${encodeURIComponent(this.topic())}`)
      );
      this.doc.set(payload);
    } catch (err: unknown) {
      const status =
        typeof err === 'object' && err !== null && 'status' in err
          ? Number((err as { status?: unknown }).status ?? 0)
          : 0;
      this.errorMessage.set(
        status === 404
          ? `No concept doc found for "${this.topic()}".`
          : 'Could not load this concept doc. Try again later.'
      );
    } finally {
      this.loading.set(false);
    }
  }
}
