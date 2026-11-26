# Deployment dashboard charts (AGT-3950)

Concept Dossier. Start at `index.html`, the decision page; `views.html`, `stack.html` and `assessments.html` are the chapters, `prototype.html` is the live prototype, and decisions and slices are in `workbench.json`. `stack.html`, section Dossier files, lists every file.

The pages are their own source. `build.mjs` refreshes only the regions between `<!--g:KEY-->` and `<!--/g-->` (style, navigation, tables, numbers, figures) and, given a packages directory, bundles `src/` into `prototype.html`. From the repository root:

```sh
npm install --prefix /tmp/ddc-bench --ignore-scripts echarts@6.1.0 echarts-gl@2.1.0 uplot@1.6.32 chart.js@4.6.2 chartjs-chart-matrix@3.1.0 chartjs-chart-sankey@0.24.1 chartjs-plugin-zoom@3.0.2 three@0.187.0
node docs/deployment-dashboard-charts/bench/run.mjs /tmp/ddc-bench   # framework and 3D measurements -> bench/results.json
node docs/deployment-dashboard-charts/build.mjs /tmp/ddc-bench       # prototype.html, bench/chunks.json, page regions, review budget
node docs/deployment-dashboard-charts/capture.mjs                    # assets/*.png and capture-manifest.json
node docs/deployment-dashboard-charts/verify.mjs /tmp/ddc-bench      # checks and budget measurements -> assets/prototype-metrics.json
node docs/deployment-dashboard-charts/build.mjs /tmp/ddc-bench       # again, to take the measurements into the pages
```

Packages are installed outside the repository; only uPlot (MIT) is bundled into `prototype.html`. Without a packages directory `build.mjs` keeps `prototype.html` and refreshes the pages only. esbuild and Playwright come from `frontend/node_modules`.

## Rules that keep the Dossier reviewable

- Every authored file stays under 40,000 characters as a diff (the review cuts there); `build.mjs` and `verify.mjs` fail otherwise. `prototype.html` is the one generated file above it; its sources are in `src/`.
- `prototype.html` is one file with inline script because the Studio's Dossier viewer runs inline scripts only. Lazy loading is therefore measured on the split build of the same sources (`build.mjs` writes it to `<packages-dir>/split`, `verify.mjs` serves it).
- Other Dossiers are named by repository path, never hyperlinked: one relative link pulls that Dossier into the review snapshot of this one. The links into other Dossiers and the Nordstern map are slice DDC-8.
- Colours and fonts of the prototype are the Studio tokens, resolved from `frontend/src/styles/_tokens-*.scss` by `tokens.mjs`. Do not add a colour value to `src/proto.css`.
- The data is synthetic and seeded (`src/data.js`). The "installation by role" set follows the card's description; it is not read from a live API.
