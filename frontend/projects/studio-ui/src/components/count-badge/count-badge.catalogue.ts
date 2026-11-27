import { CountBadgeComponent } from './count-badge.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "count-badge",
  "states": [
    {
      "id": "zero",
      "inputs": {
        "value": 0
      }
    },
    {
      "id": "some",
      "inputs": {
        "value": 3
      }
    },
    {
      "id": "overflow",
      "inputs": {
        "value": "99+"
      }
    },
    {
      "id": "active",
      "inputs": {
        "value": 3,
        "tone": "active"
      }
    },
    {
      "id": "tab",
      "inputs": {
        "value": 3,
        "variant": "pane-tab"
      }
    }
  ],
  "documentation": "# count-badge\n\nUse `import { CountBadgeComponent } from '@studio/ui'` and `<sui-count-badge>`. Existing `app-` aliases render the same implementation.\n\nPurpose: count badge primitive, moved with its current appearance.\n\nInputs: value, tone, variant. The controller contains the input types and defaults.\n\nStates: zero, some, overflow, active, tab. See the adjacent `count-badge.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"value\": 0\n};\n\n<sui-count-badge [value]=\"demo.value\"></sui-count-badge>\n```\n",
  "overlay": false
},
  component: CountBadgeComponent,
};
