# info-button

Use `import { InfoButtonComponent } from '@studio/ui'` and `<sui-info-button>`. Existing `app-` aliases render the same implementation.

Purpose: info button primitive, moved with its current appearance.

Inputs: topic, eyebrow, label, fallbackTitle. The controller contains the input types and defaults.

States: closed. See the adjacent `info-button.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

Host integration: provide `STUDIO_HELP_RENDERER` with a lazy component loader accepting a `source` input. The app keeps task-reference navigation in its host renderer.

## Example

```ts
readonly demo = {
  "topic": "catalogue",
  "label": "About this component",
  "eyebrow": "Help",
  "fallbackTitle": "Component help"
};

<sui-info-button [topic]="demo.topic" [label]="demo.label" [eyebrow]="demo.eyebrow" [fallbackTitle]="demo.fallbackTitle"></sui-info-button>
```

Help loading: provide `STUDIO_HELP_LOADER` with a function from topic to a document promise. The Studio host owns HTTP, and the catalogue supplies in-memory loading, error and document states.
