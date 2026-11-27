import { ConfirmDialogComponent } from './confirm-dialog.component';
import type { CatalogueEntry } from '../../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "confirm-dialog",
  "states": [
    {
      "id": "danger",
      "inputs": {}
    },
    {
      "id": "primary",
      "inputs": {}
    },
    {
      "id": "typed",
      "inputs": {}
    },
    {
      "id": "reason",
      "inputs": {}
    }
  ],
  "documentation": "# confirm-dialog\n\nUse `import { ConfirmDialogComponent } from '@studio/ui'` and `<sui-confirm-dialog>`. Existing `app-` aliases render the same implementation.\n\nPurpose: confirm dialog primitive, moved with its current appearance.\n\nInputs: none. The controller contains the input types and defaults.\n\nStates: danger, primary, typed, reason. See the adjacent `confirm-dialog.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\nMount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.\n\n## Example\n\n```ts\n<sui-confirm-dialog ></sui-confirm-dialog>\n```\n",
  "overlay": true
},
  component: ConfirmDialogComponent,
};
