import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { UsageHeaderHarnessComponent } from './app/usage-header-harness.component';

bootstrapApplication(UsageHeaderHarnessComponent, {
  providers: [provideZonelessChangeDetection()],
}).catch((err) => console.error(err));
