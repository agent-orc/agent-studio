import { ApplicationConfig, ENVIRONMENT_INITIALIZER, ErrorHandler, inject, provideBrowserGlobalErrorListeners, isDevMode } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideServiceWorker } from '@angular/service-worker';
import { provideCodingAgentChat } from 'coding-agent-chat';
import { ModalErrorHandler } from './services/error-dialog.service';
import { clientIdInterceptor } from './services/client-id.interceptor';
import { gitStateStampInterceptor } from './services/git-state-stamp.interceptor';
import { offlineGuardInterceptor } from './services/offline-guard.interceptor';
import { publicDemoGuardInterceptor } from './services/public-demo-guard.interceptor';
import { sessionSecurityInterceptor } from './services/session-security.interceptor';
import { TaskReferenceNavigationService } from './services/task-reference-navigation.service';
import { MediaLightboxService } from './services/media-lightbox.service';
import { TaskReferenceMicrocardHydratorService } from './services/task-reference-microcard-hydrator.service';
import { ProviderAuthStatusService } from './features/remote-hosts';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([
      offlineGuardInterceptor,
      publicDemoGuardInterceptor,
      sessionSecurityInterceptor,
      clientIdInterceptor,
      gitStateStampInterceptor,
    ])),
    { provide: ErrorHandler, useClass: ModalErrorHandler },
    // coding-agent-chat host seams: markdown task-reference auto-linking and
    // click-to-enlarge images route into the app's existing root services.
    provideCodingAgentChat({
      taskReferences: TaskReferenceNavigationService,
      mediaLightbox: MediaLightboxService,
    }),
    {
      provide: ENVIRONMENT_INITIALIZER,
      multi: true,
      useValue: () => inject(TaskReferenceMicrocardHydratorService).start(),
    },
    {
      provide: ENVIRONMENT_INITIALIZER,
      multi: true,
      useValue: () => inject(ProviderAuthStatusService).start(),
    },
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
