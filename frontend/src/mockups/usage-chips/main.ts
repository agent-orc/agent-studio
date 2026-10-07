import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { UsageAlarmsGalleryComponent } from './app/usage-alarms-gallery.component';
import { UsageChipsGalleryComponent } from './app/usage-chips-gallery.component';
import { UsageDetailHarnessComponent } from './app/usage-detail-harness.component';

// `?view=detail` mounts the HUC-S3 detail harness, `?view=alarms` the HUC-S5
// alarm gallery; the default is the HUC-S2 chip gallery.
const view = new URLSearchParams(location.search).get('view');
const root = view === 'detail' ? UsageDetailHarnessComponent : view === 'alarms' ? UsageAlarmsGalleryComponent : UsageChipsGalleryComponent;
if (view === 'detail') document.body.replaceChildren(document.createElement('mockup-usage-detail-harness'));
if (view === 'alarms') document.body.replaceChildren(document.createElement('mockup-usage-alarms-gallery'));

bootstrapApplication(root, {
  providers: [provideZonelessChangeDetection()],
}).catch((err) => console.error(err));
