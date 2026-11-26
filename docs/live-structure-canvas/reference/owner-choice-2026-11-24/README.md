# Owner choice, 24 November 2026: live structure view prototype A

This folder is a reference copy, not product code. It is the prototype the
owner chose on 24 November 2026 after comparing four prototypes of the live
structure view blind. It was delivered by AGT-3961 and is copied here
unchanged (AGT-4024) so that reviewers and later rounds can see what
"build it like that" refers to.

## What the owner said

- Chosen for its content: the technical richness of detail, the card details,
  the zoom, the mix of abstraction and concrete use, and the outline.
- Not wanted: its colours ("the typical Claude colours"). The product view
  uses the Studio tokens and the style guide instead.
- Not wanted: layers that are easy to miss and look alike. The product view
  gives each of the five layers its own visual treatment and a labelled
  switch.
- Added the same day: zooming in shows more facts on the node itself
  (semantic zoom in steps).

## What is here

| Path | Content |
|---|---|
| `index.html` | Entry page. Open it in a browser, `file://` works. |
| `css/app.css` | The prototype's own token set and layout. |
| `js/model.js` | Sample structure and the two hand-placed layouts (wide, tall). |
| `js/sim.js` | Simulated data. Nothing is bound to a real installation. |
| `js/render.js` | Canvas renderer with semantic zoom. |
| `js/ui.js` | Camera, gestures, layer toggles, breadcrumb, key plan, detail panel. |
| `screenshots/` | The 17 screenshots delivered with the prototype. |

The font files of the prototype (`fonts/`, IBM Plex) are deliberately not
copied. `css/app.css` still names them, so the page falls back to the system
fonts. Sizes and wrapping therefore differ slightly from the screenshots.

## How it relates to the product

The product view lives in `frontend/src/app/features/structure/` and is
reachable in Operations, Structure. It takes the content and behaviour from
this prototype and none of its colours, fonts or simulated values. Do not
import from this folder and do not ship `js/sim.js`.
