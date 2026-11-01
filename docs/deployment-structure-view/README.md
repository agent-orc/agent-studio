# Deployment structure view (AGT-3349)

Concept Dossier. Open [index.html](index.html); slices are in [workbench.json](workbench.json).

`index.html` is self-contained and generated. Edit the sources, then rebuild and check from the repository root:

```sh
node docs/deployment-structure-view/build.mjs     # src/* -> index.html (figures and prototype)
node docs/deployment-structure-view/verify.mjs    # browser check, light and dark, 390 and 1440 px
node docs/deployment-structure-view/capture.mjs   # evidence screenshots; needs npm --prefix frontend run build
```

| File | Role |
|---|---|
| `src/render.js` | Renderer: projection, zoom levels, nested layout, link routing, SVG |
| `src/scenarios.js` | Role-named scenario data for every figure and the prototype |
| `src/prototype.js` | Prototype controls and keyboard handling |
| `src/figure.css` | Diagram styles on the article template tokens |
| `src/index.template.html` | Dossier text with figure placeholders |
| `assets/` | Evidence captures and `capture-manifest.json` |
