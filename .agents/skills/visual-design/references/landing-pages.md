# Landing pages, heroes, and product screens

## The calm hero (owner rule)

The hero is the first viewport. It stays calm and says one thing.

- **Colour:** solid royal blue `#2c50d0` as the hero's one colour: a solid
  field, a solid block beside the statement, or the primary button. No
  gradients, no glow, no mesh or noise backgrounds, no glass.
- **Statement line:** one large, dark statement in the display face, two or
  three lines on desktop, readable in one glance on a phone. Dark ink on a
  light ground. Text that sits directly on the blue field is white: dark ink
  on `#2c50d0` reaches only about 3:1 and fails body-text contrast. In the
  dark theme the statement becomes light ink on a dark ground, and the blue
  stays solid.
- **Buttons:** flat pill buttons (`border-radius: 999px`), no shadow, no
  gradient. One primary pill and at most one secondary pill. The primary on
  a light ground is royal blue with white text; on the blue field it is white
  with royal blue text.
- **Budget:** brand, statement, one short supporting sentence, one button
  group, and at most one visual (a diagram, a product view, or an
  illustration that explains). No stat strip, logo cloud, badge, sticker, or
  second call to action in the first viewport.
- A sticky header counts against the first viewport; size the hero with
  `calc(100svh - <header height>)` or overlay the header.

Example tokens:

```css
:root {
  --hero-blue: #2c50d0;
  --ink: #14161c;
  --ground: #ffffff;
  --on-blue: #ffffff;
}
@media (prefers-color-scheme: dark) {
  :root { --ink: #eef0f6; --ground: #0f1218; }
}
.pill {
  border: 0;
  border-radius: 999px;
  padding: 0.75rem 1.4rem;
  background: var(--hero-blue);
  color: var(--on-blue);
  font-weight: 600;
}
```

## After the hero

- Each section has one job (explain, prove, show detail, or convert), one
  heading, and usually one short supporting sentence.
- Colour, type, and diagrams are welcome: a diagram that shows how the
  product works beats three paragraphs and an icon row.
- Default to layout without cards: sections, columns, lists, media blocks.
  Use a card only when the card is an interaction (a selectable plan, a
  link target).
- A typical order: hero, how it works (diagram), one concrete proof, detail,
  final call to action. Skip what the brief does not need.
- If cutting a third of the copy improves the page, cut it.

## Existing pages and product screens

- Keep the existing structure: navigation, grid, component vocabulary, and
  tokens. Change what the brief names, and the immediate neighbours that
  must change with it.
- Read the product's tokens before adding any colour or size; add a token
  only when no existing one fits, and add it in the token file, not inline.
- In a product UI, use utility copy: headings say what the area is or what
  the user can do there. No hero on a working screen unless the brief asks.
- In this repository, the rules in
  [style-guide-hard-rules.md](../../../../docs/quality/design/style-guide-hard-rules.md),
  the [admin design guideline](../../../../docs/operations/admin-design-guideline/index.html),
  and [frontend/AGENTS.md](../../../../frontend/AGENTS.md) win over anything
  here.

## Typography

- One or two families. If two, make them clearly different (a characterful
  display face plus a quiet text face, or a serif with a sans).
- A deliberate type scale (for example a 1.25 ratio) with fixed roles:
  display, heading, body, label, numbers. Numbers in tables and KPIs use
  `tabular-nums`.
- Body text 16 to 18 px, line length below about 75 characters, line height
  1.5 for sans and a little more for serif.
- Sentence case for headings, buttons, and labels.

## Self-hosted fonts

- Ship the font files with the work, in `woff2`, under a `fonts/` folder.
  Sources: the font's official repository or the `@fontsource/*` npm
  packages, which contain the `woff2` files. Check the licence (most open
  fonts are SIL OFL 1.1, which allows bundling) and keep the licence file
  next to the fonts.
- Load with `@font-face`, `font-display: swap`, and a `unicode-range` subset
  when the file is large. Preload the one face the hero needs.
- Never link `fonts.googleapis.com` or another font CDN at runtime.

```css
@font-face {
  font-family: 'Display';
  src: url('fonts/display-700.woff2') format('woff2');
  font-weight: 700;
  font-display: swap;
}
```

## Light and dark

- One token set, two value sets. Dark is designed, not inverted: lower the
  surface contrast steps, raise the ink slightly off white, and desaturate
  large colour fields a little if they glare.
- Images and diagrams work on both grounds (transparent SVG with
  `currentColor`, or one asset per theme).
- Check both themes in the screenshots; the dark theme is where low-contrast
  borders and forgotten hard-coded colours show up.

## Copy

- Write from the user's side, in plain active language. A button says what
  happens ("Start a trial", "Download the report"), and the confirmation uses
  the same verb.
- Errors say what happened and how to fix it; empty states invite the next
  step.
