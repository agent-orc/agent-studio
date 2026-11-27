# dialog

Use `import { DialogComponent } from '@studio/ui'` and `<sui-dialog>`. Existing `app-` aliases render the same implementation.

Purpose: dialog primitive, moved with its current appearance.

Inputs: eyebrow, title, subtitle, role, width, widthTier, closable, kind, size, testid, stackManagedExternally, portalToBody. The controller contains the input types and defaults.

States: default, danger, primary, large, unclosable. See the adjacent `dialog.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "Review this action",
  "subtitle": "A short decision",
  "kind": "default"
};

<sui-dialog [title]="demo.title" [subtitle]="demo.subtitle" [kind]="demo.kind"></sui-dialog>
```

Render conditionally. On `closeRequest` or an allowed `backdropClick`, unmount it. Project the body and a `[footer]` action group. Escape uses the same close output; the host must handle it. `portalToBody` and focus trapping stay enabled by default.
