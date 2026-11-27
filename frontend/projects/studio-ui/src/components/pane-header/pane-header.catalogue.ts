import { PaneHeaderComponent } from './pane-header.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "pane-header",
  "states": [
    {
      "id": "default",
      "inputs": {
        "title": "Overview"
      }
    },
    {
      "id": "maximized",
      "inputs": {
        "title": "Overview",
        "maximized": true
      }
    },
    {
      "id": "fixed",
      "inputs": {
        "title": "Overview",
        "maximizable": false,
        "hideable": false
      }
    }
  ],
  "documentation": "# pane-header\n\nUse `import { PaneHeaderComponent } from '@studio/ui'` and `<sui-pane-header>`. Existing `app-` aliases render the same implementation.\n\nPurpose: pane header primitive, moved with its current appearance.\n\nInputs: icon, iconEmoji, title, maximized, maximizable, hideable, subtitle, testid, modifier. The controller contains the input types and defaults.\n\nStates: default, maximized, fixed. See the adjacent `pane-header.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"Overview\"\n};\n\n<sui-pane-header [title]=\"demo.title\"></sui-pane-header>\n```\n",
  "overlay": false
},
  component: PaneHeaderComponent,
};
