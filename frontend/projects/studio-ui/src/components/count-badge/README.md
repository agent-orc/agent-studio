# count-badge

Use `import { CountBadgeComponent } from '@studio/ui'` and `<sui-count-badge>`. Existing `app-` aliases render the same implementation.

Purpose: count badge primitive, moved with its current appearance.

Inputs: value, tone, variant. The controller contains the input types and defaults.

States: zero, some, overflow, active, tab. See the adjacent `count-badge.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "value": 0
};

<sui-count-badge [value]="demo.value"></sui-count-badge>
```
