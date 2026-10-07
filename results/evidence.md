# HUC-S6 visual evidence

The mocked Playwright fixture in `frontend/e2e/board/huc-s6-board-detail-rhythm.spec.ts` captures the product UI from this task checkout. Each `before` image applies the previous scoped lane or protocol chrome to the same fixture; each `after` image shows the delivered styles. These are synthetic comparison captures, not historical production screenshots.

Files in the task results directory follow these names for each width (390, 1024, 1728 CSS px) and theme (light, dark):

- `board-before-<width>-<theme>--mocked.png` and `board-after-<width>-<theme>--mocked.png`: lane alignment, visible task children, and shared counts.
- `detail-before-<width>-<theme>--mocked.png` and `detail-after-<width>-<theme>--mocked.png`: task controls, contextual facts, protocol section rhythm, and phone stacking.

Playwright assertions cover lane counts, phone lane navigation without task mutation, archived run selection, pane bounds, and facts/control separation. Captures were made at 780 CSS px viewport height with light and dark tokens.

The existing prompt/facts pane precedes the protocol pane in DOM order, so phone keeps facts above protocol for reading and keyboard order. The existing task lane selector navigates to another task without a move request. A new phone-only single-lane board mode is outside this slice.
