import { NotificationStackComponent } from './notification-stack.component';
import type { CatalogueEntry } from '../../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "notification-stack",
  "states": [
    {
      "id": "default",
      "inputs": {}
    },
    {
      "id": "actions",
      "inputs": {}
    }
  ],
  "documentation": "# notification-stack\n\nUse `import { NotificationStackComponent } from '@studio/ui'` and `<sui-notification-stack>`. Existing `app-` aliases render the same implementation.\n\nPurpose: notification stack primitive, moved with its current appearance.\n\nInputs: none. The controller contains the input types and defaults.\n\nStates: default, actions. See the adjacent `notification-stack.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\nMount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.\n\n## Example\n\n```ts\n<sui-notification-stack ></sui-notification-stack>\n```\n",
  "overlay": false
},
  component: NotificationStackComponent,
};
