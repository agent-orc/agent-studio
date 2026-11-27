import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DOCUMENT, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { PendingButtonDirective, AppTooltipDirective, ResizableSplitterDirective, ConfirmDialogService, NotificationService, MediaLightboxService, InfoButtonComponent, TreeRowComponent } from '@studio/ui';
import { catalogue } from '../catalogue-entries';

@Component({
  selector: 'app-ui-catalogue',
  standalone: true,
  imports: [TreeRowComponent, MarkdownViewComponent, NgComponentOutlet, PendingButtonDirective, AppTooltipDirective, ResizableSplitterDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './catalogue.html',
  styleUrl: './catalogue.scss',
})
export class CatalogueComponent {
  readonly entries = catalogue;
  private readonly query = new URLSearchParams(location.search);
  readonly entry = catalogue.find(entry => entry.id === this.query.get('component')) ?? catalogue[0];
  readonly state = this.entry.states.find(state => state.id === this.query.get('state')) ?? this.entry.states[0];
  readonly visible = signal(true);
  readonly document = inject(DOCUMENT);
  private readonly outlet = viewChild(NgComponentOutlet);
  private readonly notifications = inject(NotificationService);
  private readonly confirms = inject(ConfirmDialogService);
  private readonly media = inject(MediaLightboxService);
  // Slot order follows the moved component's ng-content declaration. These are
  // native projected nodes, not lookalike catalogue components.
  readonly content = this.entry.id === 'row'
    ? [[this.text('Label')], [this.text('Value')], [this.text('Detail')]]
    : [[this.text('Projected content')], [this.text('Details')], [this.text('Actions')], [this.text('Footer')]];

  constructor() {
    if (this.entry.id === 'sidesheet' && this.state.id === 'closed') this.visible.set(false);
    const theme = this.query.get('theme') ?? 'light';
    this.document.documentElement.dataset['studioTheme'] = theme;
    effect(onCleanup => {
      const instance = this.outlet()?.componentInstance as { closeRequest?: { subscribe(fn: () => void): { unsubscribe(): void } } } | undefined;
      if (instance instanceof InfoButtonComponent && this.state.id !== 'closed') untracked(() => instance.toggle(new Event('click')));
      const subscription = instance?.closeRequest?.subscribe(() => this.visible.set(false));
      onCleanup(() => subscription?.unsubscribe());
    });
    if (this.entry.id === 'confirm-dialog') {
      void this.confirms.confirm({ title: 'Confirm this action', message: 'Review the affected record before continuing.',
        kind: this.state.id === 'primary' ? 'primary' : 'danger',
        ...(this.state.id === 'typed' ? { requireTypedValues: ['record'], requireTypedPrompt: 'Type record to confirm' } : {}),
        ...(this.state.id === 'reason' ? { requireReason: { prompt: 'Why is this needed?', minLength: 10 } } : {}),
      });
    }
    if (this.entry.id === 'notification-stack') {
      this.notifications.notify({ kind: 'success', title: 'Saved', message: 'The record was saved.', durationMs: 0,
        ...(this.state.id === 'actions' ? { actions: [{ label: 'Undo', callback: () => undefined }] } : {}),
      });
    }
    if (this.entry.id === 'media-lightbox' && this.state.id !== 'closed') {
      // Same-origin, product-shipped asset; no network dependency.
      const image = { src: '/favicon.ico', alt: 'Studio product icon' };
      if (this.state.id === 'gallery') this.media.openGallery({ images: [image, image], index: 0 });
      else this.media.open(image);
    }
  }

  link(component: string, state?: string): string {
    return `?component=${encodeURIComponent(component)}&state=${encodeURIComponent(state ?? '')}&theme=${this.document.documentElement.dataset['studioTheme'] ?? 'light'}`;
  }
  go(component: string): void { location.href = this.link(component); }
  changeComponent(event: Event): void { location.href = this.link((event.target as HTMLSelectElement).value); }
  toggleTheme(): void {
    this.document.documentElement.dataset['studioTheme'] = this.document.documentElement.dataset['studioTheme'] === 'light' ? 'dark' : 'light';
  }
  private text(value: string): Node { return this.document.createTextNode(value); }
}
