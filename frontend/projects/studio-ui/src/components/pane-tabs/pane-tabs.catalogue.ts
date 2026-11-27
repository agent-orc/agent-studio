import { PaneTabsComponent } from './pane-tabs.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "pane-tabs",
  "states": [
    {
      "id": "header",
      "inputs": {
        "tabs": [
          {
            "id": "overview",
            "label": "Overview"
          },
          {
            "id": "history",
            "label": "History",
            "badge": 3
          },
          {
            "id": "busy",
            "label": "Loading",
            "indicator": "spinner"
          },
          {
            "id": "disabled",
            "label": "Unavailable",
            "disabled": true
          }
        ],
        "panelId": "catalogue-panel",
        "activeTabId": "overview"
      }
    },
    {
      "id": "pill",
      "inputs": {
        "tabs": [
          {
            "id": "overview",
            "label": "Overview"
          },
          {
            "id": "history",
            "label": "History"
          }
        ],
        "panelId": "catalogue-panel",
        "activeTabId": "history",
        "variant": "pill"
      }
    }
  ],
  "documentation": "# pane-tabs\n\nUse `import { PaneTabsComponent } from '@studio/ui'` and `<sui-pane-tabs>`. Existing `app-` aliases render the same implementation.\n\nPurpose: pane tabs primitive, moved with its current appearance.\n\nInputs: tabs, activeTabId, variant, listModifier, ariaLabel, panelId, minimumTabWidth. The controller contains the input types and defaults.\n\nStates: header, pill. See the adjacent `pane-tabs.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"tabs\": [\n    {\n      \"id\": \"overview\",\n      \"label\": \"Overview\"\n    },\n    {\n      \"id\": \"history\",\n      \"label\": \"History\",\n      \"badge\": 3\n    },\n    {\n      \"id\": \"busy\",\n      \"label\": \"Loading\",\n      \"indicator\": \"spinner\"\n    },\n    {\n      \"id\": \"disabled\",\n      \"label\": \"Unavailable\",\n      \"disabled\": true\n    }\n  ],\n  \"panelId\": \"catalogue-panel\",\n  \"activeTabId\": \"overview\"\n};\n\n<sui-pane-tabs [tabs]=\"demo.tabs\" [panelId]=\"demo.panelId\" [activeTabId]=\"demo.activeTabId\"></sui-pane-tabs>\n```\n\n`tabs`, `activeTabId` and `panelId` are required. Handle `tabChange` to update the active id. Arrow keys, Home and End navigate; disabled tabs are skipped.\n",
  "overlay": false
},
  component: PaneTabsComponent,
};
