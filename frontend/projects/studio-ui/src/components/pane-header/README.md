# pane-header

Use `import { PaneHeaderComponent } from '@studio/ui'` and `<sui-pane-header>`. Existing `app-` aliases render the same implementation.

Purpose: pane header primitive, moved with its current appearance.

Inputs: icon, iconEmoji, title, maximized, maximizable, hideable, subtitle, testid, modifier. The controller contains the input types and defaults.

States: default, maximized, fixed. See the adjacent `pane-header.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "Overview"
};

<sui-pane-header [title]="demo.title"></sui-pane-header>
```
