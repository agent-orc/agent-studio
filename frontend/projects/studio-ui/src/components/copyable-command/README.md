# copyable-command

Use `import { CopyableCommandComponent } from '@studio/ui'` and `<sui-copyable-command>`. Existing `app-` aliases render the same implementation.

Purpose: copyable command primitive, moved with its current appearance.

Inputs: command, testId, label, location. The controller contains the input types and defaults.

States: default, location. See the adjacent `copyable-command.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "command": "npm run ui:lint"
};

<sui-copyable-command [command]="demo.command"></sui-copyable-command>
```
