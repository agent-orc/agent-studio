# Interactive 3D scenes

Good 3D comes from light, material, and camera far more than from polygon
count. Decide those three first.

## Lighting

- Use a key light that casts the main shadow, a softer fill from the other
  side, and a rim or back light to separate the subject from the background.
  An environment map (a small HDR or a generated room environment) gives
  reflections and soft ambient light; a single ambient light alone looks flat.
- Use one shadow-casting light. Fit its shadow camera tightly around the
  play area and use a shadow map of 1024 or 2048 px.
- Set physically based rendering correctly: sRGB output colour space, a tone
  mapping curve (ACES or AgX), and colours authored in sRGB.
- Choose the time of day or mood on purpose and let the light colour carry
  it (warm key and cool fill, or the reverse).

## Materials

- Use PBR materials (`MeshStandardMaterial` or `MeshPhysicalMaterial` in
  three.js). Vary roughness and metalness between surfaces; one shiny accent
  among matte surfaces reads better than everything glossy.
- Keep a small palette, the same discipline as in 2D: one ground, one or two
  surface tones, one accent for what the user should look at.
- Add detail with normal or roughness variation, not with more geometry.
- Fog or a background colour that matches the horizon helps depth and hides
  the far plane.

## Camera framing

- Field of view 35 to 50 degrees for objects and scenes, wider only for
  first-person play.
- Frame the subject with space around it; put the focal point off centre
  when it helps the composition.
- Orbit controls have limits: no going under the floor, no zooming through
  the subject, damping on. Clamp the near and far planes to the scene.
- The camera follows a player smoothly (damped), stays ahead of the movement
  direction, and never hides the player behind geometry.
- On resize, update the aspect ratio and the renderer size; never stretch.

## Performance budget

Measure; do not guess. Targets for a scene that also runs on a phone:

| Item | Budget |
|---|---|
| Frame time | 16.7 ms (60 fps) on desktop, at most 33 ms on a phone |
| Draw calls | about 100 to 200; merge or instance repeated meshes |
| Triangles | below about 500 k visible |
| Textures | 2048 px maximum, compressed (KTX2) when there are many |
| Pixel ratio | `renderer.setPixelRatio(Math.min(devicePixelRatio, 2))` |
| Post-processing | one or two passes; bloom only on what should glow |

- Reuse geometries and materials; dispose of what you remove.
- Pause rendering when the tab is hidden or the scene is static.
- Handle `webglcontextlost` with a visible message and a reload or restart
  option.

## State and restart

Every interactive scene has an explicit state machine, visible to the user:

`loading -> ready -> playing <-> paused -> over -> (restart) -> ready`

- Loading shows progress, not a blank canvas.
- Ready shows what to do (controls, goal) and one start action.
- Paused freezes simulation and shows resume and restart.
- Over shows the result and a restart that resets every piece of state
  without a page reload.
- `R` (or a visible button) restarts from any state; `Esc` or `P` pauses.

## Accessibility and motion

- Respect `prefers-reduced-motion`: no camera shake, slower or no automatic
  camera moves.
- Keep HUD text in HTML over the canvas, so it is crisp and readable, using
  the same tokens and fonts as the rest of the page.

## Capturing 3D

- Headless Chromium renders WebGL in software; the capture script enables
  it. If a capture shows a black canvas while the scene works in a browser,
  create the renderer with `preserveDrawingBuffer: true` for the capture, or
  run the capture with `--headed`.
- Expose the [test hooks](games.md#test-hooks) so a capture shows a known
  state, not a random frame.
