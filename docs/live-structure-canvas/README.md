# Live structure canvas (AGT-3955)

Concept Dossier. Open [index.html](index.html); decisions and slices are in [workbench.json](workbench.json).

`index.html` is self-contained and generated. The build inlines the prototype minified (esbuild from `frontend/node_modules`) and fails above 230,000 characters, so the concept review snapshot (256,000 characters a file) reads the whole Dossier; `src/` is the readable source. Edit the sources, then rebuild and check from the repository root:

```sh
node docs/live-structure-canvas/build.mjs     # src/*, workbench.json, bench results -> index.html, schema/
node docs/live-structure-canvas/capture.mjs   # assets/: today's view (needs npm --prefix frontend run build), layers, levels, replay video
node docs/live-structure-canvas/verify.mjs    # browser check, light and dark, 390 and 1440 px; writes assets/real-scene-metrics.json
node docs/live-structure-canvas/build.mjs     # again, to take the real-scene metrics into the Budget table
```

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
| `schema/` | Generated JSON Schema of the view specification and an example |
| `assets/` | Evidence captures, replay recording, `capture-manifest.json`, `real-scene-metrics.json` |
