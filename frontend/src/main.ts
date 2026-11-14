import { bootstrapApplication } from '@angular/platform-browser';
import { effect, inject, runInInjectionContext } from '@angular/core';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { ShellAccessService } from './app/services/shell-access.service';
import { applyDevModeIfFlagged } from './dev-mode';

bootstrapApplication(App, appConfig)
  .then(app => runInInjectionContext(app.injector, () => {
    const access = inject(ShellAccessService);
    let checked = false;
    effect(() => {
      if (access.operator() && !checked) {
        checked = true;
        void applyDevModeIfFlagged();
      }
    });
  }))
  .catch(err => console.error(err));
