import { MediaLightboxComponent } from './media-lightbox.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "media-lightbox",
  "states": [
    {
      "id": "closed",
      "inputs": {}
    },
    {
      "id": "image",
      "inputs": {}
    },
    {
      "id": "gallery",
      "inputs": {}
    }
  ],
  "documentation": "# media-lightbox\n\nUse `import { MediaLightboxComponent } from '@studio/ui'` and `<sui-media-lightbox>`. Existing `app-` aliases render the same implementation.\n\nPurpose: media lightbox primitive, moved with its current appearance.\n\nInputs: none. The controller contains the input types and defaults.\n\nStates: closed, image, gallery. See the adjacent `media-lightbox.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\nMount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.\n\n## Example\n\n```ts\n<sui-media-lightbox ></sui-media-lightbox>\n```\n",
  "overlay": true
},
  component: MediaLightboxComponent,
};
