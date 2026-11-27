# confirm-dialog

Use `import { ConfirmDialogComponent } from '@studio/ui'` and `<sui-confirm-dialog>`. Existing `app-` aliases render the same implementation.

Purpose: confirm dialog primitive, moved with its current appearance.

Inputs: none. The controller contains the input types and defaults.

States: danger, primary, typed, reason. See the adjacent `confirm-dialog.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

Mount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.

## Example

```ts
<sui-confirm-dialog ></sui-confirm-dialog>
```
