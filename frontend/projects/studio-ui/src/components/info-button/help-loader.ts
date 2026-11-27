import { InjectionToken } from '@angular/core';

export interface HelpDocument {
  readonly topic: string;
  readonly title: string;
  readonly body: string;
}

export const STUDIO_HELP_LOADER = new InjectionToken<(topic: string) => Promise<HelpDocument>>('Studio help loader', {
  providedIn: 'root', factory: () => () => Promise.reject({ status: 404 }),
});
