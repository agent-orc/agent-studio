import { DisclosureMarkerComponent } from './disclosure-marker.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "disclosure-marker",
  "states": [
    {
      "id": "closed",
      "inputs": {
        "open": false
      }
    },
    {
      "id": "open",
      "inputs": {
        "open": true
      }
    }
  ],
  "documentation": "# disclosure-marker\n\nUse `import { DisclosureMarkerComponent } from '@studio/ui'` and `<sui-disclosure-marker>`. Existing `app-` aliases render the same implementation.\n\nPurpose: disclosure marker primitive, moved with its current appearance.\n\nInputs: open, size. The controller contains the input types and defaults.\n\nStates: closed, open. See the adjacent `disclosure-marker.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"open\": false\n};\n\n<sui-disclosure-marker [open]=\"demo.open\"></sui-disclosure-marker>\n```\n",
  "overlay": false
},
  component: DisclosureMarkerComponent,
};
