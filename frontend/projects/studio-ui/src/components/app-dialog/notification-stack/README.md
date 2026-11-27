# notification-stack

Use `import { NotificationStackComponent } from '@studio/ui'` and `<sui-notification-stack>`. Existing `app-` aliases render the same implementation.

Purpose: notification stack primitive, moved with its current appearance.

Inputs: none. The controller contains the input types and defaults.

States: default, actions. See the adjacent `notification-stack.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

Mount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.

## Example

```ts
<sui-notification-stack ></sui-notification-stack>
```
