# Live structure canvas (AGT-3955)

Concept Dossier. Open [index.html](index.html); decisions and slices are in [workbench.json](workbench.json).

`index.html` is self-contained and generated; `src/` is the readable source. Edit the sources, then rebuild and check from the repository root:

```sh
node docs/live-structure-canvas/build.mjs     # src/*, workbench.json, bench results, assets -> index.html, schema/; checks the review snapshot
node docs/live-structure-canvas/capture.mjs   # assets/: today's view (needs npm --prefix frontend run build, else kept from the last run), layers, levels, replay video, filmstrip
node docs/live-structure-canvas/verify.mjs    # static and browser check, light and dark, 390 and 1440 px; writes assets/real-scene-metrics.json
node docs/live-structure-canvas/build.mjs     # again, to take the real-scene metrics into the Budget table
```

## Review snapshot

The concept review reads `index.html`, `workbench.json` and every file they link through a relative `href`, `src` or Markdown link: at most 256,000 characters a file, 512,000 in total and 64 files, images included (`backend/Features/Runner/ConceptDossierReviewMaterial.cs`). A file over a limit is left out and the review cannot be completed. Three rules keep the whole Dossier inside:

- The build inlines the prototype minified (esbuild from `frontend/node_modules`) and fails when `index.html` passes 240,000 characters.
- Other Dossiers are named by repository path in `code`, never hyperlinked. One relative link to another Dossier pulls that Dossier and everything it links into the snapshot.
- `index.html` links its own evidence files and lists `src/` by name, because the same code is already in the page.

`review-snapshot.mjs` reproduces the snapshot (`node docs/live-structure-canvas/review-snapshot.mjs` prints it). `build.mjs` and `verify.mjs` fail when it names a file as missing, over budget, not inspected or outside this Dossier.

## Benchmark

Renderer and chart benchmark (packages are installed outside the repository):

```sh
npm install --prefix /tmp/lsc-bench --ignore-scripts pixi.js@8.23.0 cytoscape@3.35.0 three@0.187.0 uplot@1.6.32 echarts@6.1.0 chart.js@4.6.1
node docs/live-structure-canvas/bench/run.mjs /tmp/lsc-bench   # writes bench/results.json
```

| File | Role |
|---|---|
| `src/engine.js` | Canvas 2D engine: layout, camera, semantic zoom, 2D and 2.5D projection, link bundling, layer cache, particles, tokens, effects, hit testing |
| `src/layers.js` | The seven layers: data, encoding, legend and drawing hooks |
| `src/spec.js` | `structure-view-spec/v1` schema and validator (single source of `schema/`) |
| `src/model.js` | Role-named facts of the installation, recorded deltas, persona specifications |
| `src/app.js` | Prototype controller: controls, personas, replay, minimap, search, keyboard, outline |
| `src/canvas.css`, `src/prototype.html`, `src/index.template.html` | Styles on the article tokens, prototype markup, Dossier text |
| `bench/` | Synthetic scene, candidate adapters, chart dashboards, runner and `results.json` |
| `review-snapshot.mjs` | Port of the concept review's snapshot walk and limits; used by the build and the browser check |
| `schema/` | Generated JSON Schema of the view specification and an example |
| `assets/` | Evidence captures, replay recording, `capture-manifest.json`, `real-scene-metrics.json` |
