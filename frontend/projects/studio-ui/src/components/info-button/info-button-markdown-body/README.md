# info-button-markdown-body

Use `import { InfoButtonMarkdownBodyComponent } from '@studio/ui'` and `<sui-info-button-markdown-body>`. Existing `app-` aliases render the same implementation.

Purpose: info button markdown body primitive, moved with its current appearance.

Inputs: source. The controller contains the input types and defaults.

States: default. See the adjacent `info-button-markdown-body.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "source": "# Component help\n\nThis is the same renderer used by the Studio help button."
};

<sui-info-button-markdown-body [source]="demo.source"></sui-info-button-markdown-body>
```
