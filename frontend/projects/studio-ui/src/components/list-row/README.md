# list-row

Use `import { ListRowComponent } from '@studio/ui'` and `<sui-list-row>`. Existing `app-` aliases render the same implementation.

Purpose: list row primitive, moved with its current appearance.

Inputs: label, interactive, active, capitalize, indent, testid. The controller contains the input types and defaults.

States: default, interactive, active. See the adjacent `list-row.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "label": "A record"
};

<sui-list-row [label]="demo.label"></sui-list-row>
```
