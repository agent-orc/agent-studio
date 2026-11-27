import { SectionHeaderComponent } from './section-header.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "section-header",
  "states": [
    {
      "id": "default",
      "inputs": {
        "title": "Records",
        "count": 3
      }
    },
    {
      "id": "divider",
      "inputs": {
        "title": "Records",
        "count": 3,
        "divider": true
      }
    },
    {
      "id": "expanded",
      "inputs": {
        "title": "Records",
        "count": 3,
        "collapsible": true
      }
    },
    {
      "id": "collapsed",
      "inputs": {
        "title": "Records",
        "count": 0,
        "collapsible": true,
        "collapsed": true
      }
    }
  ],
  "documentation": "# section-header\n\nUse `import { SectionHeaderComponent } from '@studio/ui'` and `<sui-section-header>`. Existing `app-` aliases render the same implementation.\n\nPurpose: section header primitive, moved with its current appearance.\n\nInputs: title, count, icon, testid, divider, collapsible, collapsed. The controller contains the input types and defaults.\n\nStates: default, divider, expanded, collapsed. See the adjacent `section-header.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"Records\",\n  \"count\": 3\n};\n\n<sui-section-header [title]=\"demo.title\" [count]=\"demo.count\"></sui-section-header>\n```\n",
  "overlay": false
},
  component: SectionHeaderComponent,
};
