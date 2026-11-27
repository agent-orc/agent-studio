import type { CatalogueEntry } from '../../catalogue';
export const entry: CatalogueEntry = {
  "id": "resizable-splitter",
  "component": null,
  "states": [
    {
      "id": "default",
      "inputs": {}
    },
    {
      "id": "vertical",
      "inputs": {}
    }
  ],
  "documentation": "# resizable-splitter\n\nUse the existing directive on a native element. Keep native semantics. States: default, vertical.\n\nKeyboard: button or separator native semantics; tooltip on focus and hover. Do not recreate the directive in a feature.\n\n## Example\n\n```ts\n<div appResizableSplitter splitterStorageKey=\"my-pane\" splitterCssProperty=\"--my-pane-width\" role=\"separator\" aria-label=\"Resize panes\" tabindex=\"0\"></div>\n```\n",
  "overlay": false
};
