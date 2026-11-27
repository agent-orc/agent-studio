import { ApplicationConfig, ENVIRONMENT_INITIALIZER, ErrorHandler, effect, inject, provideBrowserGlobalErrorListeners, isDevMode } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideServiceWorker } from '@angular/service-worker';
import { STUDIO_TOOLTIP_RENDERER } from '@studio/ui/components/tooltip/legacy-tooltip.directive';
import { STUDIO_HELP_RENDERER } from '@studio/ui/components/info-button/info-button-markdown-body/info-button-markdown-body.component';
import { STUDIO_HELP_LOADER, type HelpDocument } from '@studio/ui/components/info-button/help-loader';
import { TooltipController } from 'coding-agent-chat/shared';
import { CHAT_MEDIA_LIGHTBOX } from 'coding-agent-chat/shared';
import { ModalErrorHandler } from './services/error-dialog.service';
import { clientIdInterceptor } from './services/client-id.interceptor';
import { offlineGuardInterceptor } from './services/offline-guard.interceptor';
import { publicDemoGuardInterceptor } from './services/public-demo-guard.interceptor';
import { sessionSecurityInterceptor } from './services/session-security.interceptor';
import { shellReadInterceptor } from './services/shell-read.interceptor';
import { buildIdentityInterceptor } from './services/build-identity.interceptor';
import { MediaLightboxService } from '@studio/ui/services/media-lightbox.service';
import { TaskReferenceMicrocardHydratorService } from './services/task-reference-microcard-hydrator.service';
import { DossierReferenceHydratorService } from './services/dossier-reference-hydrator.service';
import { ProviderAuthStatusService } from './features/remote-hosts';
import { ShellAccessService } from './services/shell-access.service';

export const appConfig: ApplicationConfig = {
  providers: [
    { provide: STUDIO_HELP_LOADER, useFactory: () => {
      const http = inject(HttpClient);
      return (topic: string) => firstValueFrom(http.get<HelpDocument>(`/api/concept-docs/${encodeURIComponent(topic)}`));
    } },
    { provide: STUDIO_TOOLTIP_RENDERER, useExisting: TooltipController },
    { provide: STUDIO_HELP_RENDERER, useValue: () => import('./components/info-button/info-button-markdown-body/info-button-markdown-body.component').then(m => m.InfoButtonMarkdownBodyComponent) },
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([
      offlineGuardInterceptor,
      publicDemoGuardInterceptor,
      shellReadInterceptor,
      sessionSecurityInterceptor,
      clientIdInterceptor,
      buildIdentityInterceptor,
    ])),
    { provide: ErrorHandler, useClass: ModalErrorHandler },
    // Click-to-enlarge images route into the app's existing root service.
    // Markdown task-key links are provided by the views that render Markdown.
    { provide: CHAT_MEDIA_LIGHTBOX, useExisting: MediaLightboxService },
    {
      provide: ENVIRONMENT_INITIALIZER,
      multi: true,
      useValue: () => inject(TaskReferenceMicrocardHydratorService).start(),
    },
    {
      provide: ENVIRONMENT_INITIALIZER,
      multi: true,
      useValue: () => inject(DossierReferenceHydratorService).start(),
    },
    {
      provide: ENVIRONMENT_INITIALIZER,
      multi: true,
      useValue: () => {
        const access = inject(ShellAccessService);
        const providerAuth = inject(ProviderAuthStatusService);
        effect(() => {
          if (access.operator()) providerAuth.start();
          else providerAuth.stop();
        });
      },
    },
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
