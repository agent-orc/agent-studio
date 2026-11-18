# Small games

A small game is fun because of one mechanic that feels good, not because of
many features. Get the mechanic right with grey boxes, then dress it.

## One strong mechanic

- Name the mechanic in one sentence ("Steer a paper boat by tilting the
  wind; gusts push everything on screen"). If it needs two sentences, cut.
- The first 30 seconds teach it by play, not by a text wall. Controls are
  shown on the start screen and once more in context, then never again.
- Every element on screen relates to the mechanic. Remove what does not.

## Immediate feedback

- Every input produces a visible reaction in the same frame: movement,
  animation, sound, or particles.
- Hits, pickups, and failures each get a distinct response: a short flash, a
  small screen shake (a few pixels, under 150 ms), a hit pause of 30 to 80 ms,
  a sound with a mute toggle. Keep it proportional; feedback that hides the
  play is worse than none.
- Score and important state changes animate in the HUD so the player notices
  them.

## Difficulty curve

- Start easy enough that a first-time player succeeds within ten seconds.
- Ramp one variable at a time (speed, density, enemy behaviour), with short
  plateaus so the player can enjoy mastery before the next step.
- Failure feels fair: the player can see why they failed. Use generous
  player hitboxes (slightly smaller than the sprite), coyote time and input
  buffering for jumps, and no off-screen hazards.
- Show the best score (stored in `localStorage`) so a restart has a goal.

## States and restart

- Explicit states: title, playing, paused, game over. The game over screen
  shows the result, the best score, and how to restart.
- Restart with one key (`R`, `Space`, or `Enter`) and one button; it resets
  all state without a page reload.
- Pause with `P` or `Esc`, and when the tab loses focus.
- Fullscreen toggle on `F` where it helps; resize keeps the aspect ratio and
  the input mapping.

## Controls

- Keyboard: arrows and WASD both work; `Space` for the main action.
- Touch at phone width: on-screen controls or tap and swipe gestures, sized
  at least 44 x 44 CSS px and placed out of the play area's key region.
- Mouse aim and click when the mechanic needs it.

## Visuals

- Draw the background on the canvas; keep on-screen text minimal during play.
- A clear figure-ground contrast: the player and hazards read at a glance.
  Avoid very dark scenes unless darkness is the mechanic.
- One palette and one shape language, as for any other surface; the HUD uses
  the page's fonts and tokens.
- Use `requestAnimationFrame` with a fixed simulation step (for example
  1/60 s) and render interpolation, so speed does not depend on frame rate.

## Test hooks

Expose two functions so captures and automated checks see a known state.
Both come from OpenAI's `develop-web-game` skill (Apache-2.0, see
[sources.md](sources.md)):

```js
// Concise JSON of what is on screen: mode, player, entities, score, timers.
// State the coordinate system (origin, axis directions) in the payload.
window.render_game_to_text = () => JSON.stringify({
  mode: state.mode,
  coords: 'origin top-left, x right, y down, px',
  player: { x: state.player.x, y: state.player.y },
  entities: state.entities.map((e) => ({ kind: e.kind, x: e.x, y: e.y })),
  score: state.score,
});

// Deterministic time step: the game loop uses this when present.
window.advanceTime = (ms) => {
  const steps = Math.max(1, Math.round(ms / (1000 / 60)));
  for (let i = 0; i < steps; i++) update(1 / 60);
  render();
};
```

The capture script calls `window.advanceTime(ms)` for `--advance-ms` when it
exists (and waits in real time otherwise) and stores the output of
`window.render_game_to_text()` in its report.

## Verification

- Capture the title screen, a moment of play, and the game over screen, in
  both themes and both widths:

  ```sh
  node .agents/skills/visual-design/scripts/capture.mjs --target ./game/ \
    --name play --keys "Enter,ArrowRight,Space" --advance-ms 4000
  ```

- Play through every state at least once yourself through the hooks: start,
  score, fail, restart, and check that the state after restart equals the
  state after a fresh load.
- Check that the text state and the screenshot agree.
