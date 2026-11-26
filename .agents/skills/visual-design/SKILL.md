---
name: visual-design
description: Use for UI work whose acceptance is visual quality - dashboards and charts, structure and topology canvases, interactive 3D scenes, small browser games, landing pages, or a visible redesign of an existing screen. Sets a visual direction first, builds, captures desktop and phone screenshots in light and dark, critiques them against a rubric that scores content first for technical views (depth of detail, details per card, zoom levels, outline, abstraction mixed with concrete use) and then originality, design, feel, and technical execution, iterates at least once, and delivers with the screenshots. Studio views use the Studio's own style tokens, never a palette of their own.
trigger: user
mutates_code: true
mutates_queue: false
sentinel: TASKBOARD-SKILL-VISUAL-DESIGN
---

# Skill: Visual Design

> Sentinel: `TASKBOARD-SKILL-VISUAL-DESIGN`. Echo this string in your first reply when the orchestrator probes whether the skill loaded.

Make visual work that looks deliberate and feels good to use, and prove it with
screenshots you looked at yourself. This skill is a loop rather than a style:
decide a direction, build, look, judge, fix, look again. Most of the gap between
a good result and a generic one closes in the second round, so the second
round is mandatory.

## When to invoke

Invoke this skill when someone will accept or reject the work by looking at it:

- Dashboards, KPI rows, and charts.
- Structure and topology canvases: graphs, dependency maps, flow diagrams,
  trees, timelines you can zoom and pan.
- Interactive 3D scenes (WebGL, three.js, Babylon, raw canvas).
- Small browser games.
- Landing pages, product pages, Dossier-style explainer pages.
- A visible redesign or a new screen in an existing product.

Do not invoke it for a pure logic fix, a copy change, or a one-token colour fix.
Those follow the repository's normal rules.

## Content first for technical views

For structure, topology, operations, and other technical views (anything an
operator reads to understand or run a system), content wins over looks. The
owner picks the variant with the most technical detail, not the calmest one.
Plan, build, and score these five criteria before any visual rule:

1. **Technical depth of detail.** Real names, kinds, versions, ports, rates,
   sizes, and states, each figure with its unit, visible on the canvas itself
   and not only in a side panel.
2. **Details per card.** Every node or card says what it is, what it does,
   its key live figures, its warnings with the next step, and what it connects
   to. An empty or pattern-only card at a readable size is a defect.
3. **Zoom levels.** Zooming in adds detail (figures, then items such as task
   ids, then history and facts); it never only magnifies. The overview keeps
   names and the headline figure. Breadcrumb and a minimap show where you are.
4. **Navigable outline or structure.** An outline or tree of the whole system,
   visible by default, with a figure per row, that selects and frames a node.
   A legend explains every line and mark on screen.
5. **Abstraction mixed with concrete use.** The structural picture (boundary,
   nesting, links) and concrete use (current tasks, recent events, live
   traffic) appear together at every zoom level.

Visual rules (colour, type, calm, motion) come after these and must not cost
detail. If a visual fix would remove a figure, a label, the outline, or a zoom
tier, place the detail better instead. A round that scores lower on Content
than the round before is not delivered. Anchors: [rubric.md](references/rubric.md#0-content).

## Hard rules

These are the owner's preferences and the quality floor. They win over taste,
over the reference skills in [sources](references/sources.md), and over any
default you would otherwise use. The brief's explicit words win over these
rules.

1. **Respect the existing structure.** In an existing page or product, keep its
   layout, navigation, components, and tokens, and change only what the brief
   asks for. Do not restyle everything. In this repository, the design rules in
   [style-guide-hard-rules.md](../../../docs/quality/design/style-guide-hard-rules.md)
   and [frontend/AGENTS.md](../../../frontend/AGENTS.md) apply on top of this
   skill.
2. **Studio views use Studio tokens, never a palette of their own.** A view,
   screen, or prototype for the Studio takes every colour, font, spacing, and
   radius from the existing Studio style: the semantic `--studio-*` tokens in
   [_tokens-semantic.scss](../../../frontend/src/styles/_tokens-semantic.scss)
   (index: [tokens.md](../../../docs/quality/frontend/style-guide/tokens.md)),
   both themes, and the
   [style-guide hard rules](../../../docs/quality/design/style-guide-hard-rules.md).
   A standalone prototype copies those token names and values for both themes
   into one block; it does not invent hex values. A meaning the tokens lack
   (for example a layer ink) is derived from an existing token and named in
   the review log. Avoid the typical model default palette above all: warm
   cream or paper ground, terracotta or burnt-orange accent, serif display
   type. Outside the Studio, colour, expressive type, and diagrams are welcome;
   use a real palette with named roles, not grey on white.
3. **Calm hero areas.** A hero uses solid royal blue `#2c50d0` as its one
   colour, a large dark statement line, and flat pill buttons. No gradients,
   no glow, no glass, no floating badges or stat strips in the hero. The dark
   statement line sits on a light ground. Text placed directly on the blue
   field is white, because dark ink on `#2c50d0` reaches only about 3:1. In
   the dark theme the statement line turns light and the blue stays solid.
   Details: [landing-pages.md](references/landing-pages.md).
4. **Self-host fonts.** Ship the `woff2` files with the work and load them with
   `@font-face` and `font-display: swap`. No Google Fonts or other font CDN
   link at runtime. Use at most two families, and make them clearly different.
5. **Light and dark theme, always.** Both themes come from one token set
   (CSS custom properties). Follow `prefers-color-scheme` by default and offer
   a toggle where the surface has a settings or header area. Every screenshot
   set covers both themes.
6. **Motion explains, it does not decorate.** Animate what changed, what
   opened, and what the user's action caused. No idle floating, pulsing, or
   parallax for its own sake. Everything collapses to zero duration under
   `prefers-reduced-motion: reduce`.
7. **No coloured left accent bar** on cards, panels, rows, banners, or
   callouts. Encode status with a background tint, a badge, or a dot.
8. **No broken layout and no console errors at delivery.** No horizontal
   scroll at phone width, no overlapping text, no clipped buttons, and no
   error in the console or uncaught page error.
9. **Deliver with evidence.** The final reply names the visual direction, the
   rubric scores of the last round, and the screenshot paths. Screenshots go
   to `$JOB_RESULTS_DIR` when it is set (managed runs), otherwise to a
   `visual-review/` folder next to the work.

## Process

Write the review log as you go, using the template in
[rubric.md](references/rubric.md#review-log-template). Save it next to the
screenshots as `visual-review.md`.

### 1. Classify the surface and read its craft guide

| Surface | Read |
|---|---|
| Dashboard, chart, KPI row | [charts.md](references/charts.md) |
| Structure or topology canvas | [canvases.md](references/canvases.md) |
| Interactive 3D scene | [3d-scenes.md](references/3d-scenes.md) |
| Small game (2D or 3D) | [games.md](references/games.md), plus 3d-scenes.md for 3D |
| Landing page, hero, explainer page | [landing-pages.md](references/landing-pages.md) |
| Screen in an existing product | landing-pages.md (typography and copy), then the product's own style guide |

Always read [rubric.md](references/rubric.md).

### 2. Gather references

- **Existing work first.** When you change an existing surface, capture it
  before touching anything (step 5 with a `before` name). Read its tokens,
  fonts, and component vocabulary.
- **Subject matter.** Name the subject, the audience, and the one job of the
  surface. Distinctive choices come from the subject's world (materials,
  vocabulary, physical analogues), not from a generic web template.
- **Two to four named references.** Products, games, publications, or design
  systems that solved a similar problem well, with one sentence each on what
  you take from them. Describe them; do not copy their assets or hot-link
  their images.

### 3. Decide the visual direction in one paragraph

For a technical view, first list what each zoom level and each card type
shows (the five content criteria above). Then write one paragraph that a
designer could argue with: mood, the one memorable
element, palette, type, layout idea, and the motion idea. Then a compact token
sheet:

- Palette: for a Studio view, the `--studio-*` tokens you use per role
  (ground, surface, ink, muted ink, accent, status set). Only outside the
  Studio: four to six named hex values with roles, for light and dark.
- Type: families, roles (display, heading, body, label, numbers), and scale.
- Layout: one sentence plus a rough ASCII sketch of desktop and phone.
- Motion: which two or three changes animate, with durations.

Check the paragraph against the [anti-patterns](#anti-patterns). If a part
reads like what you would produce for any similar brief, change that part and
say what you changed. Spend boldness in one place; keep the rest quiet.

### 4. Build

- Put the tokens in one place first, then build against them.
- Build the real content: real labels, real numbers with units, real copy.
  Placeholder copy makes a design look templated.
- Games and 3D: build the state machine (loading, ready, playing, paused, over,
  restart) before the polish, and expose the test hooks described in
  [games.md](references/games.md#test-hooks) so captures are deterministic.

### 5. Capture

For a standalone page, game, or scene, use the bundled capture script. It
serves a local file or folder over loopback HTTP, shoots desktop (1440 x 900)
and phone (390 x 844) in light and dark, and records console errors, page
errors, failed requests, horizontal overflow, and frame timing:

```sh
node .agents/skills/visual-design/scripts/capture.mjs --target ./index.html --name round1
# Game or 3D: also shoot a state after input, using the test hooks
node .agents/skills/visual-design/scripts/capture.mjs --target ./game/ --name round1 \
  --click "#start" --keys "Space,ArrowRight,ArrowRight" --advance-ms 3000
```

Run `node .agents/skills/visual-design/scripts/capture.mjs --help` for every
option. The script exits with code 3 when it found a technical issue, so a
red run cannot be mistaken for a clean one. It needs the `playwright` npm
package and its Chromium; in this repository it reuses `frontend/node_modules`.

For a screen inside this repository's Angular app, follow
[frontend/AGENTS.md](../../../frontend/AGENTS.md#ui-verification--playwright-is-mandatory-after-visual-changes):
write or extend a Playwright spec, run it with
`npm --prefix frontend run e2e -- <spec>`, and save light and dark screenshots
at desktop and phone width into the results folder.

### 6. Critique against the rubric

Open every screenshot and look at it. Generating a PNG is not reviewing it.
For a technical view, score Content first. Score each dimension from 1 to 5 using the anchors in
[rubric.md](references/rubric.md), cite the screenshot behind each score, and
list the three defects that cost the most. Read the capture report: any
console error, page error, or overflow caps Technical execution at 2.

### 7. Iterate at least once

Fix the most expensive defects first, capture again with a new name
(`round2`), and score again. Never fix a design or feel defect by removing
detail. Repeat until every dimension scores 4 or more
and the capture report is clean, or stop after the fourth round and list what
is still open. Never deliver round 1 as final.

### 8. Deliver

The final reply contains:

- The visual direction paragraph.
- The rubric table of the first and the last round, so the improvement shows.
- The final screenshot paths (desktop and phone, light and dark, plus any
  state after input) and the path of `visual-review.md`.
- Known gaps, stated plainly.

## Anti-patterns

Each of these is a default rather than a choice. Use one only when the brief
asks for it.

**Generic generated look**

- Gradient washes, glows, glass panels, or neon edges as decoration.
- Content chopped into identical rounded cards with the same soft shadow;
  dashboards built as a mosaic of cards. Use a card only when the card itself
  is the interaction.
- A tracked-out uppercase eyebrow above every heading, `01 / 02 / 03` markers
  on content that is not a sequence, one accented word in each headline.
- The model default palette (warm cream ground, terracotta accent, serif
  display), or near-black with one acid accent, used regardless of subject.
  In the Studio any palette of your own is wrong; use the Studio tokens.
- The same fade-and-slide-up entrance on every section; hover lift on every
  card.
- Default font stacks (Inter, Roboto, Arial, system UI) on a surface whose
  acceptance is visual quality, when no existing product font applies.
- Emoji as icons; icon rows that add no meaning.

**Owner-specific**

- A technical view made calm by removing detail: anonymous boxes or bare
  meters at the overview, figures without units, the outline hidden behind a
  button, zoom that only magnifies.
- Restyling a whole page when the brief asked for one part.
- Busy hero: gradient, illustration collage, stat strip, logo cloud, or more
  than one button group.
- Fonts loaded from a CDN at runtime.
- One theme only, or a dark theme that is the light theme inverted.
- A coloured left accent bar as a status or decoration device.

**Craft failures**

- Charts without axis labels or units, legends far from the data, 3D pies,
  dual axes without need, rainbow palettes. See [charts.md](references/charts.md).
- Canvases that show every label at every zoom level, or that jump without
  animating a layout change. See [canvases.md](references/canvases.md).
- 3D scenes lit by a single ambient light, with every material the same
  plastic, a camera that clips into objects, or no restart. See
  [3d-scenes.md](references/3d-scenes.md).
- Games with three half-done mechanics, no feedback on a hit, no game-over
  state, or a restart that needs a page reload. See [games.md](references/games.md).
- Delivering without looking at the screenshots, or delivering the first
  round.

## Sources

This skill adapts ideas from Anthropic's public `frontend-design` skill and
OpenAI's `frontend-skill` and `develop-web-game` skills, all Apache-2.0. What
was taken, what changed, and the licence notes are in
[references/sources.md](references/sources.md).
