import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { studioProjectSlug } from '../../../../services/studio-project-slug.util';

/** Remedy for an integration that cannot run until the Studio server has a project checkout. */
@Component({
  selector: 'app-server-checkout-remedy',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './server-checkout-remedy.component.html',
})
export class ServerCheckoutRemedyComponent {
  readonly projectName = input.required<string>();
  readonly settingsHref = computed(() => `/#/projects/${studioProjectSlug(this.projectName())}/settings`);
}
