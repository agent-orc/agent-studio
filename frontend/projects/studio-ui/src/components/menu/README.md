# menu

Use `import { MenuComponent } from '@studio/ui'` and `<sui-menu>`. Existing `app-` aliases render the same implementation.

Purpose: menu primitive, moved with its current appearance.

Inputs: items, open, anchorEl, position, placement, testIdPrefix, minWidth, ariaLabel. The controller contains the input types and defaults.

States: closed, open. See the adjacent `menu.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "open": false,
  "items": []
};

<sui-menu [open]="demo.open" [items]="demo.items"></sui-menu>
```

The host owns `open`, `items`, `anchorEl` (or viewport `position`). On `closeRequest`, set `open` to false. On `itemClick`, handle the item id. Arrow keys skip disabled items, Home/End reach the ends, Enter activates, Escape closes and restores focus.
