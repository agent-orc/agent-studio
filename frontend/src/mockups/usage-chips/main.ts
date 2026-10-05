import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { UsageChipsGalleryComponent } from './app/usage-chips-gallery.component';
import { UsageDetailHarnessComponent } from './app/usage-detail-harness.component';

// `?view=detail` mounts the HUC-S3 detail harness; the default is the HUC-S2 chip gallery.
const detail = new URLSearchParams(location.search).get('view') === 'detail';
if (detail) document.body.replaceChildren(document.createElement('mockup-usage-detail-harness'));
const root = detail ? UsageDetailHarnessComponent : UsageChipsGalleryComponent;

bootstrapApplication(root, {
  providers: [provideZonelessChangeDetection()],
}).catch((err) => console.error(err));
