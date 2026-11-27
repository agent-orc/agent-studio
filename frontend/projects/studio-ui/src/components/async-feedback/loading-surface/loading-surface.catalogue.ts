import { LoadingSurfaceComponent } from './loading-surface.component';
import type { CatalogueEntry } from '../../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "loading-surface",
  "states": [
    {
      "id": "board",
      "inputs": {
        "kind": "board",
        "label": "Loading records\u2026"
      }
    },
    {
      "id": "task-detail",
      "inputs": {
        "kind": "task-detail",
        "label": "Loading records\u2026"
      }
    },
    {
      "id": "list",
      "inputs": {
        "kind": "list",
        "label": "Loading records\u2026"
      }
    }
  ],
  "documentation": "# loading-surface\n\nUse `import { LoadingSurfaceComponent } from '@studio/ui'` and `<sui-loading-surface>`. Existing `app-` aliases render the same implementation.\n\nPurpose: loading surface primitive, moved with its current appearance.\n\nInputs: kind, label. The controller contains the input types and defaults.\n\nStates: board, task-detail, list. See the adjacent `loading-surface.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"kind\": \"board\",\n  \"label\": \"Loading records\\u2026\"\n};\n\n<sui-loading-surface [kind]=\"demo.kind\" [label]=\"demo.label\"></sui-loading-surface>\n```\n",
  "overlay": false
},
  component: LoadingSurfaceComponent,
};
