import { StudioIconComponent } from './studio-icon.component';
import type { CatalogueEntry } from '../../catalogue';

export const entry: CatalogueEntry = {
  ...{
  "id": "studio-icon",
  "states": [
    {
      "id": "folder",
      "inputs": {
        "name": "folder"
      }
    },
    {
      "id": "file",
      "inputs": {
        "name": "file"
      }
    },
    {
      "id": "small",
      "inputs": {
        "name": "search",
        "size": 12
      }
    },
    {
      "id": "large",
      "inputs": {
        "name": "settings",
        "size": 24
      }
    }
  ],
  "documentation": "# studio-icon\n\nUse `import { StudioIconComponent } from '@studio/ui'` and `<sui-studio-icon>`. Existing `app-` aliases render the same implementation.\n\nPurpose: studio icon primitive, moved with its current appearance.\n\nInputs: name, size, strokeWidth. The controller contains the input types and defaults.\n\nStates: folder, file, small, large. See the adjacent `studio-icon.catalogue.ts` for reproducible inputs.\n\nKeyboard: native controls keep their keyboard behavior. Menu and tabs own arrow navigation; overlays use the shared modal stack and focus trap, and Escape returns focus to the opener. Static rows and indicators add no Tab stop.\n\nDo: use semantic tokens and projected content. Use component outputs to update host state.\nDo not: copy its template or override its internal chrome on a page.\n\n## Example\n\n```ts\nreadonly demo = {\n  \"name\": \"folder\"\n};\n\n<sui-studio-icon [name]=\"demo.name\"></sui-studio-icon>\n```\n",
  "overlay": false
},
  component: StudioIconComponent,
};
