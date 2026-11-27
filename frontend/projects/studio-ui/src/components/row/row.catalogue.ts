import { RowComponent } from './row.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "row",
  "states": [
    {
      "id": "compact",
      "inputs": {
        "variant": "compact"
      }
    },
    {
      "id": "default",
      "inputs": {
        "variant": "default"
      }
    },
    {
      "id": "cozy",
      "inputs": {
        "variant": "cozy"
      }
    },
    {
      "id": "interactive",
      "inputs": {
        "interactive": true
      }
    }
  ],
  "documentation": "# row\n\nUse `import { RowComponent } from '@studio/ui'` and `<sui-row>`. Existing `app-` aliases render the same implementation.\n\nPurpose: row primitive, moved with its current appearance.\n\nInputs: variant, interactive. The controller contains the input types and defaults.\n\nStates: compact, default, cozy, interactive. See the adjacent `row.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\nProjection: `[appRowLabel]`, `[appRowValue]`, `[appRowTrailing]`.\n\n## Example\n\n```ts\n<sui-row variant=\"compact\">\n  <span appRowLabel>Name</span><span appRowValue>Record</span>\n</sui-row>\n```\n",
  "overlay": false
},
  component: RowComponent,
};
