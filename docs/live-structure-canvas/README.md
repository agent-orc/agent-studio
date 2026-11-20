# Live structure canvas (AGT-3955)

Concept Dossier. Start at `index.html`, the decision page; `context.html`, `layers.html`, `live.html` and `stack.html` are the chapters, `prototype.html` is the live prototype, and decisions and slices are in `workbench.json`. `stack.html`, section Dossier files, lists every file.

The pages are their own source. `build.mjs` refreshes only the regions between `<!--g:KEY-->` and `<!--/g-->` (style, navigation, tables, numbers) and writes `prototype.html` and `schema/` from `src/`. From the repository root:

```sh
node docs/live-structure-canvas/build.mjs     # regions, prototype.html, schema/; fails if the review would not receive a page whole
node docs/live-structure-canvas/capture.mjs   # assets/: layers, levels, replay recording, filmstrip; today's view needs npm --prefix frontend run build
node docs/live-structure-canvas/verify.mjs    # static and browser check, light and dark, 390 and 1440 px; writes assets/real-scene-metrics.json
node docs/live-structure-canvas/build.mjs     # again, to take the measurements into the pages
```

## Why several pages

The concept review reads a first delivery as a diff, cut to 40,000 characters a file, 580,000 characters and 12,000 lines in total (`runner/ReviewAspectInputBudget.cs`, `runner/SourceFacts/MergeBaseDiffMaterial.cs`). Its snapshot of linked files (256,000 characters a file, 512,000 in total, 64 files, `backend/Features/Runner/ConceptDossierReviewMaterial.cs`) comes from the Studio's own checkout, which holds a Dossier only after integration. Rules that keep the Dossier reviewable:

- Every authored file stays under 40,000 characters as a diff. The style block is the canonical article style, minified by the build.
- `prototype.html` is the one generated file above that limit: the Studio's Dossier viewer runs inline scripts only, so the prototype has to be a single file.
- Other Dossiers are named by repository path, never hyperlinked: one relative link pulls that Dossier and everything it links into the snapshot.
- A concept delivery changes only this directory. Links from other Dossiers to this one are slice LSC-10.

`review-input.mjs` ports both inputs with their limits and prints them; `build.mjs` and `verify.mjs` fail when a file a reviewer must read would be missing, cut or left out.

## Benchmark

Packages are installed outside the repository:

```sh
npm install --prefix /tmp/lsc-bench --ignore-scripts pixi.js@8.23.0 cytoscape@3.35.0 three@0.187.0 uplot@1.6.32 echarts@6.1.0 chart.js@4.6.1
node docs/live-structure-canvas/bench/run.mjs /tmp/lsc-bench   # writes bench/results.json
```
