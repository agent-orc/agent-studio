# Header usage cockpit

Canonical entrypoint: [index.html](index.html). Source concept: AGT-2913.

The five operator decisions dated 25 September 2026 are settled. Human sight review remains pending. `decision-pending` is the required delivery descriptor status.

## Files

- `index.html`: house article, usage/data/state/responsive contracts, evidence and decisions.
- `workbench.json`: schemaVersion 1, seven bounded implementation prompts.
- `mockup.html`: responsive offline before/after header, board and task detail; theme, surface, stage, expanded and state query parameters.
- `assets/`: 36 before/after matrix screenshots, six expanded usage screenshots and capture manifest.
- `capture.mjs`: offline Chromium capture and focused layout/keyboard checks.
- `verify.py`: descriptor, local navigation, evidence matrix and article-template verification.

## Provenance

The original screenshot binary was unavailable. Before header imagery reconstructs the operator description; board and detail before imagery is illustrative. All after images are synthetic design proposals. None is a live product screenshot. The browser-captured filenames use `--mocked` accordingly. Caption dates are 25 September 2026; fixture reference time is 16:58 Europe/Berlin. Device scale is 1, widths 390/1024/1728 CSS px, both themes, reduced motion. No application backend was started.

The product mockup embeds the CSS compiled from `frontend/src/styles/_tokens-semantic.scss` and its primitive dependencies at source revision `d713e8a156b565374166d545a0f079dc8be23926`. The Dossier style is copied verbatim from the article-document-v2 template. The mockup is for visual review: the desktop overlay uses a portable modal dialog instead of the specified production nonmodal popover, and destination/task actions are illustrative. Production interaction requirements are explicit in the Dossier and implementation prompts.

## Verification

Run from repository root, using existing frontend Playwright dependencies:

```sh
node docs/header-usage-cockpit/capture.mjs
python3 docs/header-usage-cockpit/verify.py
```

The first command writes only adjacent concept screenshots and their manifest. It checks browser errors, after-layout overflow, header heights, dialog opening, Escape and focus return. The second checks all local links and fragments, the 42-frame matrix, five decision blocks, seven bounded acceptance scopes and exact article-v2 style reuse. Product builds and product regression suites are outside this documentation-only change. Human sight review is not claimed.
