# section-header

Use `import { SectionHeaderComponent } from '@studio/ui'` and `<sui-section-header>`. Existing `app-` aliases render the same implementation.

Purpose: section header primitive, moved with its current appearance.

Inputs: title, count, icon, testid, divider, collapsible, collapsed. The controller contains the input types and defaults.

States: default, divider, expanded, collapsed. See the adjacent `section-header.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "Records",
  "count": 3
};

<sui-section-header [title]="demo.title" [count]="demo.count"></sui-section-header>
```
