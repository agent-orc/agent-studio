import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, InjectionToken, Type, inject, input, signal } from '@angular/core';

export const STUDIO_HELP_RENDERER = new InjectionToken<() => Promise<Type<unknown>>>('Studio help renderer');

@Component({
  selector: 'sui-info-button-markdown-body, app-info-button-markdown-body',
  standalone: true,
  imports: [NgComponentOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './info-button-markdown-body.component.html',
})
export class InfoButtonMarkdownBodyComponent {
  readonly source = input.required<string>();
  readonly renderer = signal<Type<unknown> | null>(null);
  constructor() {
    const load = inject(STUDIO_HELP_RENDERER, { optional: true });
    if (load) void load().then(component => this.renderer.set(component));
  }
}
