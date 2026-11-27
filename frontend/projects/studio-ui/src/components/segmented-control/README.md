# segmented-control

Use `import { SegmentedControlComponent } from '@studio/ui'` and `<sui-segmented-control>`. Existing `app-` aliases render the same implementation.

Purpose: segmented control primitive, moved with its current appearance.

Inputs: options, value, ariaLabel, disabled, rovingFocus. The controller contains the input types and defaults.

States: default, disabled. See the adjacent `segmented-control.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "options": [
    {
      "value": "overview",
      "label": "Overview"
    },
    {
      "value": "history",
      "label": "History"
    },
    {
      "value": "disabled",
      "label": "Unavailable",
      "disabled": true
    }
  ],
  "value": "overview",
  "ariaLabel": "View"
};

<sui-segmented-control [options]="demo.options" [value]="demo.value" [ariaLabel]="demo.ariaLabel"></sui-segmented-control>
```
