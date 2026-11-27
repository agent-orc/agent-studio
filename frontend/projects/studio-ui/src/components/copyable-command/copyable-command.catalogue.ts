import { CopyableCommandComponent } from './copyable-command.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "copyable-command",
  "states": [
    {
      "id": "default",
      "inputs": {
        "command": "npm run ui:lint"
      }
    },
    {
      "id": "location",
      "inputs": {
        "command": "npm run ui:test",
        "location": "frontend/"
      }
    }
  ],
  "documentation": "# copyable-command\n\nUse `import { CopyableCommandComponent } from '@studio/ui'` and `<sui-copyable-command>`. Existing `app-` aliases render the same implementation.\n\nPurpose: copyable command primitive, moved with its current appearance.\n\nInputs: command, testId, label, location. The controller contains the input types and defaults.\n\nStates: default, location. See the adjacent `copyable-command.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"command\": \"npm run ui:lint\"\n};\n\n<sui-copyable-command [command]=\"demo.command\"></sui-copyable-command>\n```\n",
  "overlay": false
},
  component: CopyableCommandComponent,
};
