# pending-button

Use the existing directive on a native element. Keep native semantics. States: idle, pending, disabled.

Keyboard: button or separator native semantics; tooltip on focus and hover. Do not recreate the directive in a feature.

## Example

```ts
<button [appPendingButton]="saving()" pendingLabel="Saving…">Save</button>
```
