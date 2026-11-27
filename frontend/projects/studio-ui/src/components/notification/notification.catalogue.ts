import { NotificationComponent } from './notification.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "notification",
  "states": [
    {
      "id": "success",
      "inputs": {
        "kind": "success",
        "title": "Success",
        "closable": true
      }
    },
    {
      "id": "info",
      "inputs": {
        "kind": "info",
        "title": "Info",
        "closable": true
      }
    },
    {
      "id": "warning",
      "inputs": {
        "kind": "warning",
        "title": "Warning",
        "closable": true
      }
    },
    {
      "id": "error",
      "inputs": {
        "kind": "error",
        "title": "Error",
        "closable": true
      }
    },
    {
      "id": "accent",
      "inputs": {
        "kind": "accent",
        "title": "Accent",
        "closable": true
      }
    },
    {
      "id": "banner",
      "inputs": {
        "layout": "banner",
        "kind": "info",
        "title": "Information"
      }
    }
  ],
  "documentation": "# notification\n\nUse `import { NotificationComponent } from '@studio/ui'` and `<sui-notification>`. Existing `app-` aliases render the same implementation.\n\nPurpose: notification primitive, moved with its current appearance.\n\nInputs: kind, layout, icon, title, closable, ariaLive, testid. The controller contains the input types and defaults.\n\nStates: success, info, warning, error, accent, banner. See the adjacent `notification.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"kind\": \"success\",\n  \"title\": \"Success\",\n  \"closable\": true\n};\n\n<sui-notification [kind]=\"demo.kind\" [title]=\"demo.title\" [closable]=\"demo.closable\"></sui-notification>\n```\n",
  "overlay": false
},
  component: NotificationComponent,
};
