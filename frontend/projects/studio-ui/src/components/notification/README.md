# notification

Use `import { NotificationComponent } from '@studio/ui'` and `<sui-notification>`. Existing `app-` aliases render the same implementation.

Purpose: notification primitive, moved with its current appearance.

Inputs: kind, layout, icon, title, closable, ariaLive, testid. The controller contains the input types and defaults.

States: success, info, warning, error, accent, banner. See the adjacent `notification.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "kind": "success",
  "title": "Success",
  "closable": true
};

<sui-notification [kind]="demo.kind" [title]="demo.title" [closable]="demo.closable"></sui-notification>
```
