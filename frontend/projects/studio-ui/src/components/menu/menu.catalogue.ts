import { MenuComponent } from './menu.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "menu",
  "states": [
    {
      "id": "closed",
      "inputs": {
        "open": false,
        "items": []
      }
    },
    {
      "id": "open",
      "inputs": {
        "open": true,
        "position": {
          "x": 48,
          "y": 160
        },
        "items": [
          {
            "kind": "header",
            "label": "Actions"
          },
          {
            "kind": "row",
            "id": "open",
            "label": "Open",
            "active": true
          },
          {
            "kind": "row",
            "id": "disabled",
            "label": "Unavailable",
            "disabled": true,
            "tooltip": "Requires permission"
          },
          {
            "kind": "separator"
          },
          {
            "kind": "row",
            "id": "delete",
            "label": "Delete",
            "danger": true
          }
        ]
      }
    }
  ],
  "documentation": "# menu\n\nUse `import { MenuComponent } from '@studio/ui'` and `<sui-menu>`. Existing `app-` aliases render the same implementation.\n\nPurpose: menu primitive, moved with its current appearance.\n\nInputs: items, open, anchorEl, position, placement, testIdPrefix, minWidth, ariaLabel. The controller contains the input types and defaults.\n\nStates: closed, open. See the adjacent `menu.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"open\": false,\n  \"items\": []\n};\n\n<sui-menu [open]=\"demo.open\" [items]=\"demo.items\"></sui-menu>\n```\n\nThe host owns `open`, `items`, `anchorEl` (or viewport `position`). On `closeRequest`, set `open` to false. On `itemClick`, handle the item id. Arrow keys skip disabled items, Home/End reach the ends, Enter activates, Escape closes and restores focus.\n",
  "overlay": true
},
  component: MenuComponent,
};
