import { ErrorDialogComponent } from './error-dialog.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "error-dialog",
  "states": [
    {
      "id": "error",
      "inputs": {
        "error": {
          "title": "Could not save",
          "message": "Try again.",
          "output": "Request failed.",
          "stackTrace": null,
          "source": "Catalogue",
          "canOpenCliConfig": false
        }
      }
    },
    {
      "id": "refusal",
      "inputs": {
        "error": {
          "title": "Action unavailable",
          "message": "This action needs permission.",
          "output": "Permission missing.",
          "stackTrace": null,
          "source": "Catalogue",
          "canOpenCliConfig": false,
          "refusal": {
            "sentence": "This action needs permission.",
            "nextAction": "Ask an owner for access."
          }
        }
      }
    }
  ],
  "documentation": "# error-dialog\n\nUse `import { ErrorDialogComponent } from '@studio/ui'` and `<sui-error-dialog>`. Existing `app-` aliases render the same implementation.\n\nPurpose: error dialog primitive, moved with its current appearance.\n\nInputs: error, canOpenCliConfig, copyButtonLabel. The controller contains the input types and defaults.\n\nStates: error, refusal. See the adjacent `error-dialog.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"error\": {\n    \"title\": \"Could not save\",\n    \"message\": \"Try again.\",\n    \"output\": \"Request failed.\",\n    \"stackTrace\": null,\n    \"source\": \"Catalogue\",\n    \"canOpenCliConfig\": false\n  }\n};\n\n<sui-error-dialog [error]=\"demo.error\"></sui-error-dialog>\n```\n",
  "overlay": true
},
  component: ErrorDialogComponent,
};
