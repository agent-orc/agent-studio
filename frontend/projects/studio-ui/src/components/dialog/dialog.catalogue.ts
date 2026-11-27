import { DialogComponent } from './dialog.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "dialog",
  "states": [
    {
      "id": "default",
      "inputs": {
        "title": "Review this action",
        "subtitle": "A short decision",
        "kind": "default"
      }
    },
    {
      "id": "danger",
      "inputs": {
        "title": "Delete this item?",
        "kind": "danger",
        "size": "sm"
      }
    },
    {
      "id": "primary",
      "inputs": {
        "title": "Continue",
        "kind": "primary"
      }
    },
    {
      "id": "large",
      "inputs": {
        "title": "Review details",
        "widthTier": "lg"
      }
    },
    {
      "id": "unclosable",
      "inputs": {
        "title": "Action in progress",
        "closable": false
      }
    }
  ],
  "documentation": "# dialog\n\nUse `import { DialogComponent } from '@studio/ui'` and `<sui-dialog>`. Existing `app-` aliases render the same implementation.\n\nPurpose: dialog primitive, moved with its current appearance.\n\nInputs: eyebrow, title, subtitle, role, width, widthTier, closable, kind, size, testid, stackManagedExternally, portalToBody. The controller contains the input types and defaults.\n\nStates: default, danger, primary, large, unclosable. See the adjacent `dialog.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"Review this action\",\n  \"subtitle\": \"A short decision\",\n  \"kind\": \"default\"\n};\n\n<sui-dialog [title]=\"demo.title\" [subtitle]=\"demo.subtitle\" [kind]=\"demo.kind\"></sui-dialog>\n```\n\nRender conditionally. On `closeRequest` or an allowed `backdropClick`, unmount it. Project the body and a `[footer]` action group. Escape uses the same close output; the host must handle it. `portalToBody` and focus trapping stay enabled by default.\n",
  "overlay": true
},
  component: DialogComponent,
};
