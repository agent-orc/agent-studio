# tree-row

Use `import { TreeRowComponent } from '@studio/ui'` and `<sui-tree-row>`. Existing `app-` aliases render the same implementation.

Purpose: tree row primitive, moved with its current appearance.

Inputs: chevron, reserveChevron, glyph, glyphChar, reserveGlyph, glyphColor, label, count, meta, tooltip, tooltipTestId, ariaLabel, ariaControls, active, level, testid, ariaCurrent, chevronTestid, disabled, hasActions, visibleName. The controller contains the input types and defaults.

States: default, active, disabled, expanded, collapsed, child. See the adjacent `tree-row.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "label": "Records",
  "glyph": "folder",
  "count": 3
};

<sui-tree-row [label]="demo.label" [glyph]="demo.glyph" [count]="demo.count"></sui-tree-row>
```
