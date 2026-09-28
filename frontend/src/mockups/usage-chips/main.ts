import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { UsageAlarmsGalleryComponent } from './app/usage-alarms-gallery.component';
import { UsageChipsGalleryComponent } from './app/usage-chips-gallery.component';

// `?view=alarms` mounts the HUC-S5 alarm gallery instead of the HUC-S2 chips.
const alarms = new URLSearchParams(location.search).get('view') === 'alarms';
if (alarms) {
  document.querySelector('mockup-usage-chips-gallery')?.replaceWith(document.createElement('mockup-usage-alarms-gallery'));
}

bootstrapApplication(alarms ? UsageAlarmsGalleryComponent : UsageChipsGalleryComponent, {
  providers: [provideZonelessChangeDetection()],
}).catch((err) => console.error(err));
