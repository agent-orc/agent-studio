# studio-sidebar-header

Use `import { StudioSidebarHeaderComponent } from '@studio/ui'` and `<sui-studio-sidebar-header>`. Existing `app-` aliases render the same implementation.

Purpose: studio sidebar header primitive, moved with its current appearance.

Inputs: title, subtitle, subtitleTestid, testid. The controller contains the input types and defaults.

States: default, subtitle. See the adjacent `studio-sidebar-header.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "title": "Records"
};

<sui-studio-sidebar-header [title]="demo.title"></sui-studio-sidebar-header>
```
