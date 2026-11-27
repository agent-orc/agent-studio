# row

Use `import { RowComponent } from '@studio/ui'` and `<sui-row>`. Existing `app-` aliases render the same implementation.

Purpose: row primitive, moved with its current appearance.

Inputs: variant, interactive. The controller contains the input types and defaults.

States: compact, default, cozy, interactive. See the adjacent `row.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

Projection: `[appRowLabel]`, `[appRowValue]`, `[appRowTrailing]`.

## Example

```ts
<sui-row variant="compact">
  <span appRowLabel>Name</span><span appRowValue>Record</span>
</sui-row>
```
