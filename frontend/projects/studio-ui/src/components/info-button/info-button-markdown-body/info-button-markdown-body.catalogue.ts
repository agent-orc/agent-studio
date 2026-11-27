import { InfoButtonMarkdownBodyComponent } from './info-button-markdown-body.component';
import type { CatalogueEntry } from '../../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "info-button-markdown-body",
  "states": [
    {
      "id": "default",
      "inputs": {
        "source": "# Component help\n\nThis is the same renderer used by the Studio help button."
      }
    }
  ],
  "documentation": "# info-button-markdown-body\n\nUse `import { InfoButtonMarkdownBodyComponent } from '@studio/ui'` and `<sui-info-button-markdown-body>`. Existing `app-` aliases render the same implementation.\n\nPurpose: info button markdown body primitive, moved with its current appearance.\n\nInputs: source. The controller contains the input types and defaults.\n\nStates: default. See the adjacent `info-button-markdown-body.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"source\": \"# Component help\\n\\nThis is the same renderer used by the Studio help button.\"\n};\n\n<sui-info-button-markdown-body [source]=\"demo.source\"></sui-info-button-markdown-body>\n```\n",
  "overlay": false
},
  component: InfoButtonMarkdownBodyComponent,
};
