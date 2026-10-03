import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { UsageChipsGalleryComponent } from './app/usage-chips-gallery.component';

bootstrapApplication(UsageChipsGalleryComponent, {
  providers: [provideZonelessChangeDetection()],
}).catch((err) => console.error(err));
