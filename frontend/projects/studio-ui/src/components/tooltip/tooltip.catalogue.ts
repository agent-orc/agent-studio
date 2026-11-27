import type { CatalogueEntry } from '../../catalogue';
export const entry: CatalogueEntry = {
  "id": "tooltip",
  "component": null,
  "states": [
    {
      "id": "hidden",
      "inputs": {}
    },
    {
      "id": "visible",
      "inputs": {}
    },
    {
      "id": "interactive",
      "inputs": {}
    }
  ],
  "documentation": "# tooltip\n\nUse the existing directive on a native element. Keep native semantics. States: hidden, visible, interactive.\n\nKeyboard: button or separator native semantics; tooltip on focus and hover. Do not recreate the directive in a feature.\n\n## Example\n\n```ts\n<button appTooltip=\"Open component help\">Help</button>\n```\n",
  "overlay": false
};
