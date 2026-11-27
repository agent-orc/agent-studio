# status-indicator

Use `import { StatusIndicatorComponent } from '@studio/ui'` and `<sui-status-indicator>`. Existing `app-` aliases render the same implementation.

Purpose: status indicator primitive, moved with its current appearance.

Inputs: tone, label, detail, compact, wrap, testId. The controller contains the input types and defaults.

States: success, warning, danger, info, neutral, running, compact, wrap. See the adjacent `status-indicator.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "tone": "success",
  "label": "Success"
};

<sui-status-indicator [tone]="demo.tone" [label]="demo.label"></sui-status-indicator>
```
