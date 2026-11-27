import { StudioSidebarHeaderComponent } from './studio-sidebar-header.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "studio-sidebar-header",
  "states": [
    {
      "id": "default",
      "inputs": {
        "title": "Records"
      }
    },
    {
      "id": "subtitle",
      "inputs": {
        "title": "Records",
        "subtitle": "Three records in this collection"
      }
    }
  ],
  "documentation": "# studio-sidebar-header\n\nUse `import { StudioSidebarHeaderComponent } from '@studio/ui'` and `<sui-studio-sidebar-header>`. Existing `app-` aliases render the same implementation.\n\nPurpose: studio sidebar header primitive, moved with its current appearance.\n\nInputs: title, subtitle, subtitleTestid, testid. The controller contains the input types and defaults.\n\nStates: default, subtitle. See the adjacent `studio-sidebar-header.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"Records\"\n};\n\n<sui-studio-sidebar-header [title]=\"demo.title\"></sui-studio-sidebar-header>\n```\n",
  "overlay": false
},
  component: StudioSidebarHeaderComponent,
};
