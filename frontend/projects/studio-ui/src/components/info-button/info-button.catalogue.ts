import { InfoButtonComponent } from './info-button.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "info-button",
  "states": [
    {
      "id": "closed",
      "inputs": {
        "topic": "catalogue",
        "label": "About this component",
        "eyebrow": "Help",
        "fallbackTitle": "Component help"
      }
    },
    {
      "id": "loading",
      "inputs": {
        "topic": "catalogue",
        "label": "About this component",
        "eyebrow": "Help",
        "fallbackTitle": "Component help"
      }
    },
    {
      "id": "error",
      "inputs": {
        "topic": "catalogue",
        "label": "About this component",
        "eyebrow": "Help",
        "fallbackTitle": "Component help"
      }
    },
    {
      "id": "open",
      "inputs": {
        "topic": "catalogue",
        "label": "About this component",
        "eyebrow": "Help",
        "fallbackTitle": "Component help"
      }
    }
  ],
  "documentation": "# info-button\n\nUse `import { InfoButtonComponent } from '@studio/ui'` and `<sui-info-button>`. Existing `app-` aliases render the same implementation.\n\nPurpose: info button primitive, moved with its current appearance.\n\nInputs: topic, eyebrow, label, fallbackTitle. The controller contains the input types and defaults.\n\nStates: closed. See the adjacent `info-button.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\nHost integration: provide `STUDIO_HELP_RENDERER` with a lazy component loader accepting a `source` input. The app keeps task-reference navigation in its host renderer.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"topic\": \"catalogue\",\n  \"label\": \"About this component\",\n  \"eyebrow\": \"Help\",\n  \"fallbackTitle\": \"Component help\"\n};\n\n<sui-info-button [topic]=\"demo.topic\" [label]=\"demo.label\" [eyebrow]=\"demo.eyebrow\" [fallbackTitle]=\"demo.fallbackTitle\"></sui-info-button>\n```\n\nHelp loading: provide `STUDIO_HELP_LOADER` with a function from topic to a document promise. The Studio host owns HTTP, and the catalogue supplies in-memory loading, error and document states.\n",
  "overlay": true
},
  component: InfoButtonComponent,
};
