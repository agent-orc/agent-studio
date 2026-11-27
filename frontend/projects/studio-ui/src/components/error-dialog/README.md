# error-dialog

Use `import { ErrorDialogComponent } from '@studio/ui'` and `<sui-error-dialog>`. Existing `app-` aliases render the same implementation.

Purpose: error dialog primitive, moved with its current appearance.

Inputs: error, canOpenCliConfig, copyButtonLabel. The controller contains the input types and defaults.

States: error, refusal. See the adjacent `error-dialog.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

## Example

```ts
readonly demo = {
  "error": {
    "title": "Could not save",
    "message": "Try again.",
    "output": "Request failed.",
    "stackTrace": null,
    "source": "Catalogue",
    "canOpenCliConfig": false
  }
};

<sui-error-dialog [error]="demo.error"></sui-error-dialog>
```
