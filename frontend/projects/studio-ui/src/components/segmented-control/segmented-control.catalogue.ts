import { SegmentedControlComponent } from './segmented-control.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "segmented-control",
  "states": [
    {
      "id": "default",
      "inputs": {
        "options": [
          {
            "value": "overview",
            "label": "Overview"
          },
          {
            "value": "history",
            "label": "History"
          },
          {
            "value": "disabled",
            "label": "Unavailable",
            "disabled": true
          }
        ],
        "value": "overview",
        "ariaLabel": "View"
      }
    },
    {
      "id": "disabled",
      "inputs": {
        "options": [
          {
            "value": "overview",
            "label": "Overview"
          },
          {
            "value": "history",
            "label": "History"
          }
        ],
        "value": "history",
        "disabled": true,
        "ariaLabel": "Disabled view"
      }
    }
  ],
  "documentation": "# segmented-control\n\nUse `import { SegmentedControlComponent } from '@studio/ui'` and `<sui-segmented-control>`. Existing `app-` aliases render the same implementation.\n\nPurpose: segmented control primitive, moved with its current appearance.\n\nInputs: options, value, ariaLabel, disabled, rovingFocus. The controller contains the input types and defaults.\n\nStates: default, disabled. See the adjacent `segmented-control.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"options\": [\n    {\n      \"value\": \"overview\",\n      \"label\": \"Overview\"\n    },\n    {\n      \"value\": \"history\",\n      \"label\": \"History\"\n    },\n    {\n      \"value\": \"disabled\",\n      \"label\": \"Unavailable\",\n      \"disabled\": true\n    }\n  ],\n  \"value\": \"overview\",\n  \"ariaLabel\": \"View\"\n};\n\n<sui-segmented-control [options]=\"demo.options\" [value]=\"demo.value\" [ariaLabel]=\"demo.ariaLabel\"></sui-segmented-control>\n```\n",
  "overlay": false
},
  component: SegmentedControlComponent,
};
