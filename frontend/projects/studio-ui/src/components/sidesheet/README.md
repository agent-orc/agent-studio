# sidesheet

Use `import { SidesheetComponent } from '@studio/ui'` and `<sui-sidesheet>`. Existing `app-` aliases render the same implementation.

Purpose: sidesheet primitive, moved with its current appearance.

Inputs: eyebrow, title, subtitle, width, closable, testid. The controller contains the input types and defaults.

States: closed, open. See the adjacent `sidesheet.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "Details"
};

<sui-sidesheet [title]="demo.title"></sui-sidesheet>
```

Render conditionally; this component has no `open` input. Its host owns push-layout width and resizing. Handle `closeRequest` by unmounting it.
