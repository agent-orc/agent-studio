# loading-surface

Use `import { LoadingSurfaceComponent } from '@studio/ui'` and `<sui-loading-surface>`. Existing `app-` aliases render the same implementation.

Purpose: loading surface primitive, moved with its current appearance.

Inputs: kind, label. The controller contains the input types and defaults.

States: board, task-detail, list. See the adjacent `loading-surface.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "kind": "board",
  "label": "Loading records\u2026"
};

<sui-loading-surface [kind]="demo.kind" [label]="demo.label"></sui-loading-surface>
```
