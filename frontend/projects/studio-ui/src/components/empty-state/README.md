# empty-state

Use `import { EmptyStateComponent } from '@studio/ui'` and `<sui-empty-state>`. Existing `app-` aliases render the same implementation.

Purpose: empty state primitive, moved with its current appearance.

Inputs: icon, title, body, compact, testid. The controller contains the input types and defaults.

States: default, compact. See the adjacent `empty-state.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "No records yet",
  "body": "Create a record to get started.",
  "icon": "folder"
};

<sui-empty-state [title]="demo.title" [body]="demo.body" [icon]="demo.icon"></sui-empty-state>
```
