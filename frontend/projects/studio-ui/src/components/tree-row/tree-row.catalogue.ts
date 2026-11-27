import { TreeRowComponent } from './tree-row.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "tree-row",
  "states": [
    {
      "id": "default",
      "inputs": {
        "label": "Records",
        "glyph": "folder",
        "count": 3
      }
    },
    {
      "id": "active",
      "inputs": {
        "label": "Current records",
        "active": true,
        "ariaCurrent": "page"
      }
    },
    {
      "id": "disabled",
      "inputs": {
        "label": "Unavailable",
        "disabled": true
      }
    },
    {
      "id": "expanded",
      "inputs": {
        "label": "Records",
        "chevron": "expanded"
      }
    },
    {
      "id": "collapsed",
      "inputs": {
        "label": "Records",
        "chevron": "collapsed"
      }
    },
    {
      "id": "child",
      "inputs": {
        "label": "Child record",
        "level": "child"
      }
    }
  ],
  "documentation": "# tree-row\n\nUse `import { TreeRowComponent } from '@studio/ui'` and `<sui-tree-row>`. Existing `app-` aliases render the same implementation.\n\nPurpose: tree row primitive, moved with its current appearance.\n\nInputs: chevron, reserveChevron, glyph, glyphChar, reserveGlyph, glyphColor, label, count, meta, tooltip, tooltipTestId, ariaLabel, ariaControls, active, level, testid, ariaCurrent, chevronTestid, disabled, hasActions, visibleName. The controller contains the input types and defaults.\n\nStates: default, active, disabled, expanded, collapsed, child. See the adjacent `tree-row.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"label\": \"Records\",\n  \"glyph\": \"folder\",\n  \"count\": 3\n};\n\n<sui-tree-row [label]=\"demo.label\" [glyph]=\"demo.glyph\" [count]=\"demo.count\"></sui-tree-row>\n```\n",
  "overlay": false
},
  component: TreeRowComponent,
};
