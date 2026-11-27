import { bootstrapApplication } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { STUDIO_TOOLTIP_RENDERER, STUDIO_HELP_RENDERER, STUDIO_HELP_LOADER } from '@studio/ui';
import { TooltipController } from 'coding-agent-chat/shared';
import { CatalogueComponent } from './catalogue/catalogue';

void bootstrapApplication(CatalogueComponent, {
  providers: [provideHttpClient(), { provide: STUDIO_HELP_LOADER, useValue: (topic: string) => {
    const state = new URLSearchParams(location.search).get('state');
    if (state === 'loading') return new Promise(() => undefined);
    if (state === 'error') return Promise.reject({ status: 404 });
    return Promise.resolve({ topic, title: 'Component help', body: '# Component help\n\nThe host supplies this document through STUDIO_HELP_LOADER.' });
  } }, { provide: STUDIO_HELP_RENDERER, useValue: () => import('./help-renderer/help-renderer').then(m => m.HelpRendererComponent) }, { provide: STUDIO_TOOLTIP_RENDERER, useExisting: TooltipController }],
});
