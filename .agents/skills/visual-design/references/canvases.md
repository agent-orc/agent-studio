# Structure and topology canvases

Graphs, dependency maps, flow diagrams, trees, and zoomable timelines. The
canvas is good when a user can find their way at every zoom level and can
follow every change with their eyes.

## Semantic zoom

Zoom changes what is shown, not only how large it is. Each level adds detail;
none strips names or figures. Define the levels before you build, for example:

| Level | Shows |
|---|---|
| Overview | Every node named, its kind, the headline figure with unit, status, warnings, main flows with rates |
| Working | Plus labelled meters with units, slots or items, ports, edge labels |
| Detail | Plus individual items (task ids, elapsed time, progress), short history, facts |

- Fixed thresholds or per-node layout for the room a node has are both fine;
  cross-fade (150 to 200 ms), never a pop.
- Hide a figure only when it truly does not fit, and then show it in the
  outline or on hover.
- Text never renders below about 11 CSS px. Below that, replace it with a
  shape or hide it.
- Keep the level of detail that the user selected or searched for: a search
  hit is labelled at every zoom level.

## Level of detail and density

- Cull off-screen elements; draw simplified shapes for small ones.
- Avoid label collisions: place labels in free space, drop the lower-priority
  label when two collide, and show it on hover.
- Use SVG up to a few hundred elements, then Canvas 2D, then WebGL for
  thousands. Measure before switching.
- Encode hierarchy with position and grouping first, then size, then colour.
  Status is a dot or a background tint, not a coloured left bar.

## Navigation

- Zoom anchors at the cursor or pinch centre. Wheel, trackpad pinch, and
  `+`/`-` keys all work; zoom has sensible minimum and maximum limits.
- Pan with drag on the background and with the arrow keys. Dragging a node
  moves the node, not the canvas.
- A visible "fit to content" control, and a double click or key that frames
  the selection.
- Selection shows a clear outline, the inspector shows details, and `Esc`
  clears it.
- An outline of the whole structure, visible by default, that selects and
  frames a node; a breadcrumb that follows the camera; a minimap for anything
  larger than one screen, placed where it never covers content at phone width.
- At phone width: one-finger pan, pinch zoom, tap to select, and the
  inspector as a bottom sheet.

## Motion that explains change

- When data or layout changes, animate nodes from their old to their new
  position (object constancy), 200 to 400 ms with an ease-out curve. The user
  should be able to follow one node through the change.
- New elements fade or grow in at their final place; removed elements fade
  out where they were. Do not fly things in from the edge.
- Camera moves (fit, focus) animate, but stay under 400 ms.
- Nothing moves while the user does nothing: no idle drifting force layout,
  no breathing nodes, no marching ants unless the dashes show real flow.
- Stop the force simulation once it settles; run it again only on change.
- Under `prefers-reduced-motion: reduce`, apply changes instantly.

## Layout

- Choose the layout from the structure: layered (Sugiyama, ELK, dagre) for
  directed flows and dependencies, tree layouts for hierarchies, force layouts
  only for genuinely undirected networks.
- Keep layouts stable: the same data gives the same picture, and an
  incremental change moves as few nodes as possible.
- Route edges around nodes; use orthogonal or gently curved edges, one style
  per canvas. Arrowheads are small and point at the receiver.

## Verification

- Capture overview, working, and detail zoom in both themes. The capture
  script's `--click` and `--keys` options can drive zoom via buttons or keys.
- Check that the overview is still readable at phone width.
- Check frame timing during a pan or zoom, not only at rest.
