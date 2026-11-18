# Charts and dashboards

A dashboard answers a small set of questions quickly. Start from those
questions, not from the chart types a library offers.

## Framework

- **One chart framework per surface.** Use the one the surface already uses.
  Pick one for a new surface (for example ECharts, Vega-Lite, Observable Plot,
  Chart.js, or D3 for custom marks) and do not mix two on one page; mixed
  frameworks give mixed tooltips, fonts, animation, and themes.
- Theme the framework once from your design tokens (font, ink, gridline,
  categorical palette, both themes) and reuse that theme for every chart.
- Use the framework's own responsive resize, not a fixed pixel size.

## Readable defaults

- Every axis has a label with its unit: `Duration (min)`, `Cost (EUR)`,
  `Requests per second`. A unit in the title counts when the chart has one
  measure.
- Bars and areas start at zero. Lines may zoom into a range, but say so.
- Numbers use `font-variant-numeric: tabular-nums`, the locale's separators,
  and a sensible precision (no `12.3456789 %`).
- Label lines and bars directly when there are five or fewer series, instead
  of a legend. Put a legend, when needed, next to the data it explains.
- Categorical palette: at most six to eight distinct hues, safe for colour
  vision deficiency, the same colour for the same category on every chart of
  the surface. Sequential data uses one hue ramp; data with a meaningful
  midpoint uses a diverging ramp.
- Gridlines are light and few; the data is the darkest thing on the chart.
- Tooltips show the exact value, unit, and the category or time; they never
  hide the point being hovered.
- Time axes use ticks a person would choose (days, weeks, hours), in the
  user's time zone.

## Dashboard layout

- One row of key numbers first, each with its comparison (previous period,
  target) and the period it covers. Then the charts that explain those
  numbers, then the detail table.
- One shared time range and filter bar for the whole surface.
- Totals equal the sum of what is visible under them. A total that does not
  reconcile with its parts is a bug.
- Group by question, not by chart type. Align charts on one grid; equal
  heights in a row.
- Utility copy: headings say what the area shows or what the user can do
  ("Failed runs by cause", "Queue age"), not marketing lines.

## Cross-filtering

- Selecting a bar, segment, legend entry, or brushed range filters or
  highlights the other charts on the surface.
- Highlight first (dim the rest) rather than removing data, so context stays.
- The active filter is visible as a removable chip, and one control clears
  all filters.
- Keep filter state in one place (a store or the URL), so every chart reads
  the same selection and a link reproduces the view.
- Animate the transition between filtered states (200 to 300 ms) so the user
  sees what moved.

## States

- Loading: a skeleton in the chart's shape, shown after about 200 ms.
- Empty: say why and what to do ("No runs in this range. Widen the range.").
- Error: say what failed and offer a retry.
- Partial data: mark the incomplete period, for example a dashed segment for
  the current day.

## No chart junk

Remove: 3D effects on 2D charts, pie charts with more than four slices,
gradient-filled bars, drop shadows, decorative icons inside charts, dual
y-axes unless the two measures are intrinsically linked, animated counters
that delay reading the number, chart borders and backgrounds that only box
things in.

## Phone width

- Charts stack in one column; nothing scrolls sideways.
- Reduce tick count and shorten labels instead of shrinking the font below
  12 px.
- Tooltips work on tap; a tap outside dismisses them.
