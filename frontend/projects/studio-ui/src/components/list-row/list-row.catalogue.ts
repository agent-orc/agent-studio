import { ListRowComponent } from './list-row.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "list-row",
  "states": [
    {
      "id": "default",
      "inputs": {
        "label": "A record"
      }
    },
    {
      "id": "interactive",
      "inputs": {
        "label": "Open record",
        "interactive": true
      }
    },
    {
      "id": "active",
      "inputs": {
        "label": "Current record",
        "interactive": true,
        "active": true
      }
    }
  ],
  "documentation": "# list-row\n\nUse `import { ListRowComponent } from '@studio/ui'` and `<sui-list-row>`. Existing `app-` aliases render the same implementation.\n\nPurpose: list row primitive, moved with its current appearance.\n\nInputs: label, interactive, active, capitalize, indent, testid. The controller contains the input types and defaults.\n\nStates: default, interactive, active. See the adjacent `list-row.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"label\": \"A record\"\n};\n\n<sui-list-row [label]=\"demo.label\"></sui-list-row>\n```\n",
  "overlay": false
},
  component: ListRowComponent,
};
