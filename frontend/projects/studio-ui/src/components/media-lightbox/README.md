# media-lightbox

Use `import { MediaLightboxComponent } from '@studio/ui'` and `<sui-media-lightbox>`. Existing `app-` aliases render the same implementation.

Purpose: media lightbox primitive, moved with its current appearance.

Inputs: none. The controller contains the input types and defaults.

States: closed, image, gallery. See the adjacent `media-lightbox.catalogue.ts` for reproducible inputs.

Keyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.

Do: use semantic tokens and projected content. Use component outputs to update host state.
Do not: copy its template or override its internal chrome on a page.

Mount once. Open it through `ConfirmDialogService`, `NotificationService` or `MediaLightboxService`, respectively. Do not create another service instance in a feature.

## Example

```ts
<sui-media-lightbox ></sui-media-lightbox>
```
