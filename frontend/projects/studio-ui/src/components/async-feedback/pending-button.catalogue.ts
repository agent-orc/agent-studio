import type { CatalogueEntry } from '../../catalogue';
export const entry: CatalogueEntry = {
  "id": "pending-button",
  "component": null,
  "states": [
    {
      "id": "idle",
      "inputs": {}
    },
    {
      "id": "pending",
      "inputs": {}
    },
    {
      "id": "disabled",
      "inputs": {}
    }
  ],
  "documentation": "# pending-button\n\nUse the existing directive on a native element. Keep native semantics. States: idle, pending, disabled.\n\nKeyboard: button or separator native semantics; tooltip on focus and hover. Do not recreate the directive in a feature.\n\n## Example\n\n```ts\n<button [appPendingButton]=\"saving()\" pendingLabel=\"Saving\u2026\">Save</button>\n```\n",
  "overlay": false
};
