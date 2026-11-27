# studio-icon

Use `import { StudioIconComponent } from '@studio/ui'` and `<sui-studio-icon>`. Existing `app-` aliases render the same implementation.

Purpose: studio icon primitive, moved with its current appearance.

Inputs: name, size, strokeWidth. The controller contains the input types and defaults.

States: folder, file, small, large. See the adjacent `studio-icon.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "name": "folder"
};

<sui-studio-icon [name]="demo.name"></sui-studio-icon>
```
