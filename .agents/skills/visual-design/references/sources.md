# Sources and attribution

This skill adapts ideas from three public skills for coding agents. All three
are published under the Apache License, Version 2.0, the same licence as this
repository (see the repository's `LICENSE`). None of their text is copied
verbatim in bulk; the guidance was condensed, rewritten, merged with the
owner's preferences, and changed where the owner's rules differ. Short code
patterns that were taken over are marked where they appear.

## Anthropic: `frontend-design`

- Source: <https://github.com/anthropics/skills/tree/main/skills/frontend-design>
  (read 2026-11-18). Licence: Apache-2.0, `LICENSE.txt` in that folder.
- Adapted:
  - Grounding the design in the subject's world, audience, and job.
  - Planning a compact token system (palette as named hex values, type
    roles, layout sketch, principles) and reviewing it against the brief for
    generic choices before building.
  - The calibration list of generated-design defaults (cream and terracotta,
    near-black with one acid accent, identical rounded cards, uppercase
    eyebrows, numbered markers on non-sequences, single accented headline
    words, fade-and-slide-up on every section).
  - Spending boldness in one place, the quality floor (responsive, focus,
    reduced motion), and critiquing with screenshots.
  - Copy guidance: user's perspective, active voice, consistent verbs, errors
    and empty states as direction.
- Changed: the owner's calm hero (solid royal blue, flat pills, no
  gradients) replaces the open-ended aesthetic risk for heroes; the
  "confirm the subject with the client" step became "name the subject" so an
  unattended run does not stop; a numeric rubric and a mandatory second round
  were added.

## OpenAI: `frontend-skill`

- Source: <https://github.com/openai/skills>, folder
  `skills/.curated/frontend-skill`, added in commit `82d2c5b` and removed in
  commit `11c6438`; read at commit `30444ae` (2026-11-18). Licence:
  Apache-2.0, `LICENSE.txt` in that folder. Background:
  <https://developers.openai.com/blog/designing-delightful-frontends-with-gpt-5-4>.
- Adapted:
  - Writing a short visual thesis and a motion plan before building.
  - One job per section; a hero budget (brand, statement, one sentence, one
    button group, one visual); the sticky-header viewport budget.
  - Cards only when the card is the interaction; no dashboard card mosaics.
  - Utility copy for product UI, and the operator litmus check.
  - Verifying with Playwright screenshots at several viewports.
- Changed: the source recommends gradients, images, or patterns instead of
  flat backgrounds and a full-bleed image hero; the owner's rules replace
  that with a solid royal blue, a dark statement line, and no gradients. The
  source asks for at least two or three motions on visually led pages; this
  skill allows motion only where it explains a change. The source's
  React and Framer Motion specifics were left out because the skill is
  framework-neutral.

## OpenAI: `develop-web-game`

- Source: <https://github.com/openai/skills>, folder
  `skills/.curated/develop-web-game`, removed in commit `11c6438`; read at
  commit `30444ae` (2026-11-18). Licence: Apache-2.0, `LICENSE.txt` in that
  folder.
- Adapted:
  - The test hooks `window.render_game_to_text()` and
    `window.advanceTime(ms)`, including their minimal code patterns, in
    [games.md](games.md#test-hooks).
  - The loop of small change, input burst, screenshot, inspection, and the
    rule to actually open the screenshots and to fix the first console error
    before continuing.
  - Keeping on-screen text minimal during play and showing controls on the
    start screen; a fullscreen toggle on `F`.
- Changed: the bundled Playwright client and action payload format were not
  taken over; this skill ships its own smaller
  [capture script](../scripts/capture.mjs) that also shoots light and dark
  themes at desktop and phone width.

## Apache-2.0 notice

Portions of this skill are derived from works licensed under the Apache
License, Version 2.0 (<https://www.apache.org/licenses/LICENSE-2.0>).
Copyright for those portions remains with their authors (Anthropic and
OpenAI). Neither upstream folder ships a `NOTICE` file, so none is carried
over. The derived text has been modified as described above.
