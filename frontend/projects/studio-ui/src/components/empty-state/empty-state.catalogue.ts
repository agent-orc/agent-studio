import { EmptyStateComponent } from './empty-state.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "empty-state",
  "states": [
    {
      "id": "default",
      "inputs": {
        "title": "No records yet",
        "body": "Create a record to get started.",
        "icon": "folder"
      }
    },
    {
      "id": "compact",
      "inputs": {
        "title": "No matches",
        "body": "Change your search.",
        "compact": true
      }
    }
  ],
  "documentation": "# empty-state\n\nUse `import { EmptyStateComponent } from '@studio/ui'` and `<sui-empty-state>`. Existing `app-` aliases render the same implementation.\n\nPurpose: empty state primitive, moved with its current appearance.\n\nInputs: icon, title, body, compact, testid. The controller contains the input types and defaults.\n\nStates: default, compact. See the adjacent `empty-state.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"title\": \"No records yet\",\n  \"body\": \"Create a record to get started.\",\n  \"icon\": \"folder\"\n};\n\n<sui-empty-state [title]=\"demo.title\" [body]=\"demo.body\" [icon]=\"demo.icon\"></sui-empty-state>\n```\n",
  "overlay": false
},
  component: EmptyStateComponent,
};
