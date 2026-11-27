# disclosure-marker

Use `import { DisclosureMarkerComponent } from '@studio/ui'` and `<sui-disclosure-marker>`. Existing `app-` aliases render the same implementation.

Purpose: disclosure marker primitive, moved with its current appearance.

Inputs: open, size. The controller contains the input types and defaults.

States: closed, open. See the adjacent `disclosure-marker.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "open": false
};

<sui-disclosure-marker [open]="demo.open"></sui-disclosure-marker>
```
