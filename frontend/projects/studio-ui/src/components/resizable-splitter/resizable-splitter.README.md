# resizable-splitter

Use the existing directive on a native element. Keep native semantics. States: default, vertical.

Keyboard: button or separator native semantics; tooltip on focus and hover. Do not recreate the directive in a feature.

## Example

```ts
<div appResizableSplitter splitterStorageKey="my-pane" splitterCssProperty="--my-pane-width" role="separator" aria-label="Resize panes" tabindex="0"></div>
```
