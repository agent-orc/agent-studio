# Visual design rubric

Score your own work on these dimensions before you deliver. For structure,
operations, and other technical views, score Content (0) first; it outranks
the four visual dimensions. They are the
owner's acceptance dimensions, so a score here predicts the review. Score from
the screenshots and the capture report, not from memory of the code.

Each dimension gets a score from 1 to 5. Ship only when every dimension is at
least 4 and the capture report is clean, or when the round budget is spent and
the open defects are listed.

| Score | Meaning |
|---|---|
| 5 | A reviewer would point at it as an example. Nothing to fix. |
| 4 | Good. Minor polish left, nothing a user would trip over. |
| 3 | Works, but looks or feels generic, or one visible flaw stands out. |
| 2 | A clear defect: a broken state, a layout break, an unreadable area. |
| 1 | Does not work or does not meet the brief. |

## 0. Content

Technical views only. Does the view carry the technical detail an operator
needs? The five criteria are defined in
[SKILL.md](../SKILL.md#content-first-for-technical-views).

- **5:** Every node shows name, kind, and live figures with units at the
  overview; cards carry role, figures, warnings with next step, and links;
  each zoom step adds detail down to items and history; an outline of the
  whole system is visible by default; concrete use (tasks, events, traffic)
  sits next to the structure at every level.
- **3:** Detail exists but hides: figures only in the panel or only at the
  deepest zoom, meters without labels or units, outline behind a button.
- **1:** A pretty diagram of empty boxes.

Checks:

- At the overview screenshot, count nodes without a name or a figure. Any at
  a readable size cost a point.
- Does each zoom level show something the level above did not?
- Can you reach every node from the outline without panning?
- A design or feel fix that removed detail is reverted, and Content may never
  drop between rounds.

## 1. Originality

Is there an interesting idea or mechanic?

- **5:** One clear idea taken from the subject's world (a metaphor, a mechanic,
  a way of showing the data) that a reviewer remembers after closing the tab.
  Everything else supports it.
- **3:** Competent and familiar. Could be the output for any similar brief.
- **1:** A template with the brief's words pasted in.

Checks:

- Can you name the one memorable element in one sentence?
- Would a different subject have produced the same design? If yes, it is a
  default, not a choice.
- For games: is there one mechanic that is fun on its own, before any polish?
- For dashboards and canvases: is the view shaped by the questions a user
  asks of the data, or by the widgets a library offers?

## 2. Design

Do colours, lighting, shapes, animation, and UI fit together?

- **5:** One palette, one type system, one shape language, and one motion
  character across the whole surface, in both themes. Hierarchy is obvious at
  a glance.
- **3:** Individually fine parts that do not belong together (two radius
  systems, mixed icon styles, an accent used for three meanings).
- **1:** Clashing, unreadable, or one theme broken.

Checks:

- Squint at the screenshot. Do the most important three things stand out in
  the right order?
- Count accent uses. Each one means the same thing.
- Text contrast meets WCAG AA (4.5:1 body, 3:1 large text and UI glyphs) in
  both themes.
- 3D: is there a key light, a readable silhouette, and more than one material?
- Studio views: every colour and font comes from the `--studio-*` tokens;
  no palette of your own, no warm cream, terracotta, or serif display default.
- Owner rules: calm hero, self-hosted fonts, no gradients in the hero, no left
  accent bars, existing structure kept.

## 3. Feel

Are controls, camera, pace, and feedback pleasant? For UIs: zoom, pan,
transitions, responsiveness.

- **5:** Every input answers within a frame, motion shows cause and effect,
  the camera or viewport goes where the user expects, and phone use is as
  good as desktop use.
- **3:** Works, but something is sluggish, abrupt, or fiddly (zoom jumps,
  hit targets too small, camera fights the player).
- **1:** Hard to control, or nothing answers the input.

Checks:

- Hover, focus, pressed, and disabled states exist and are visible; keyboard
  focus is visible.
- Touch targets are at least 44 x 44 CSS px at phone width.
- Zoom goes to the cursor or pinch point; pan has no lag; fit-to-content
  exists on canvases.
- Transitions are 120 to 300 ms for UI, and nothing animates while idle.
- Games: input to visible reaction within one frame; pace ramps up; failure
  feels fair.

## 4. Technical execution

Collisions, states, restart, performance. For UIs: no layout breaks, no
console errors, frame budget.

- **5:** Clean capture report, every state reachable and recoverable, frame
  budget held at both widths.
- **3:** Works on the happy path; one edge state or one viewport is rough.
- **1:** Crashes, errors, or a broken viewport.

Checks (most are in the capture report):

- No console errors, no uncaught page errors, no failed requests.
- No horizontal overflow at 390 px width; nothing overlaps or clips.
- Frame timing: median frame at or below 16.7 ms in the sample; long frames
  (over 50 ms) only while loading. Headless Chromium renders WebGL in
  software, so treat absolute numbers as a relative signal between rounds.
- Empty, loading, and error states exist for data surfaces.
- Games and 3D: every state in the state machine is reachable, restart
  returns to a clean state without reload, resize keeps the aspect and input
  mapping, collisions match what is drawn.
- Any console error, page error, or overflow caps this dimension at 2.

## Review log template

Copy this into `visual-review.md` next to the screenshots and fill it in as
you work.

```markdown
# Visual review: <surface name>

## Brief
<one or two sentences: subject, audience, the one job of the surface>

## References
- <reference>: <what to take from it>

## Visual direction
<one paragraph: mood, the memorable element, palette, type, layout, motion>

| Token | Light | Dark | Role |
|---|---|---|---|
| ground | #...... | #...... | page background |

## Round 1
Screenshots: <paths>
Capture report: <path>, <issue count>

| Dimension | Score | Evidence (screenshot) | Main defect |
|---|---|---|---|
| Content (technical views) | | | |
| Originality | | | |
| Design | | | |
| Feel | | | |
| Technical execution | | | |

Fixes for round 2:
1. <defect> -> <change>

## Round 2
...

## Delivered
Final screenshots: <paths>
Known gaps: <list, or "none">
```
