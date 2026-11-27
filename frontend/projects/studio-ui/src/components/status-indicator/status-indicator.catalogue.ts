import { StatusIndicatorComponent } from './status-indicator.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "status-indicator",
  "states": [
    {
      "id": "success",
      "inputs": {
        "tone": "success",
        "label": "Success"
      }
    },
    {
      "id": "warning",
      "inputs": {
        "tone": "warning",
        "label": "Warning"
      }
    },
    {
      "id": "danger",
      "inputs": {
        "tone": "danger",
        "label": "Danger"
      }
    },
    {
      "id": "info",
      "inputs": {
        "tone": "info",
        "label": "Info"
      }
    },
    {
      "id": "neutral",
      "inputs": {
        "tone": "neutral",
        "label": "Neutral"
      }
    },
    {
      "id": "running",
      "inputs": {
        "tone": "running",
        "label": "Running"
      }
    },
    {
      "id": "compact",
      "inputs": {
        "tone": "info",
        "label": "Compact",
        "compact": true
      }
    },
    {
      "id": "wrap",
      "inputs": {
        "tone": "warning",
        "label": "An action needs attention and has a longer explanation",
        "wrap": true
      }
    }
  ],
  "documentation": "# status-indicator\n\nUse `import { StatusIndicatorComponent } from '@studio/ui'` and `<sui-status-indicator>`. Existing `app-` aliases render the same implementation.\n\nPurpose: status indicator primitive, moved with its current appearance.\n\nInputs: tone, label, detail, compact, wrap, testId. The controller contains the input types and defaults.\n\nStates: success, warning, danger, info, neutral, running, compact, wrap. See the adjacent `status-indicator.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"tone\": \"success\",\n  \"label\": \"Success\"\n};\n\n<sui-status-indicator [tone]=\"demo.tone\" [label]=\"demo.label\"></sui-status-indicator>\n```\n",
  "overlay": false
},
  component: StatusIndicatorComponent,
};
