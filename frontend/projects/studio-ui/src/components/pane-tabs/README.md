# pane-tabs

Use `import { PaneTabsComponent } from '@studio/ui'` and `<sui-pane-tabs>`. Existing `app-` aliases render the same implementation.

Purpose: pane tabs primitive, moved with its current appearance.

Inputs: tabs, activeTabId, variant, listModifier, ariaLabel, panelId, minimumTabWidth. The controller contains the input types and defaults.

States: header, pill. See the adjacent `pane-tabs.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
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
};

<sui-pane-tabs [tabs]="demo.tabs" [panelId]="demo.panelId" [activeTabId]="demo.activeTabId"></sui-pane-tabs>
```

`tabs`, `activeTabId` and `panelId` are required. Handle `tabChange` to update the active id. Arrow keys, Home and End navigate; disabled tabs are skipped.
