import { SidesheetComponent } from './sidesheet.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "sidesheet",
  "states": [
    {
      "id": "closed",
      "inputs": {
        "title": "Details"
      }
    },
    {
      "id": "open",
      "inputs": {
        "title": "Details"
      }
    }
  ],
  "documentation": "# sidesheet\n\nUse `import { SidesheetComponent } from '@studio/ui'` and `<sui-sidesheet>`. Existing `app-` aliases render the same implementation.\n\nPurpose: sidesheet primitive, moved with its current appearance.\n\nInputs: eyebrow, title, subtitle, width, closable, testid. The controller contains the input types and defaults.\n\nStates: closed, open. See the adjacent `sidesheet.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"Details\"\n};\n\n<sui-sidesheet [title]=\"demo.title\"></sui-sidesheet>\n```\n\nRender conditionally; this component has no `open` input. Its host owns push-layout width and resizing. Handle `closeRequest` by unmounting it.\n",
  "overlay": true
},
  component: SidesheetComponent,
};
