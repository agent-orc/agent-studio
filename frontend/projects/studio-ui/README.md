# Studio UI

The Angular workspace library owns reusable controls, generic overlay services and the token source. The Studio app and the static Components catalogue consume the same classes through `@studio/ui`. No package build or publish step is needed.

Use a standalone component from `@studio/ui` in `imports`. New templates use `sui-`; existing `app-` aliases remain supported. Historical source paths are export-only compatibility shims. Each component folder documents its inputs, states and projection.

Run `npm run ui:test`, `npm run ui:lint` and `npm run ui:catalogue:check` from frontend. They need no backend. `npm run ui:catalogue:build` builds the catalogue. `npm run ui:inventory -- /absolute/output.json` inventories raw styles and duplicate control recipes.

Tokens enter through `src/styles/_tokens-semantic.scss`. Primitives, semantic theme roles and exact-value compatibility scales live beside it. Old `src/styles` paths forward to these implementations. Components must not import Studio services, app code or coding-agent-chat. Host-only help and legacy-tooltip renderers enter through injection tokens; the Studio retains its current implementations.

A restyle changes tokens first, then component styles, then catalogue screenshots. Pages with local chrome remain inventoried debt; they do not become library consumers merely by replacing literals.

The private package metadata only marks modules as free of top-level side effects for source-alias tree shaking; it is not a published or versioned package. Startup-only host providers import their narrow subpaths to keep controls lazy. `api-report.md` contains generated TypeScript declarations, including inputs and outputs; `node scripts/check-ui-library.mjs --write-api` refreshes it for review.

Screenshot goldens use the runner's pinned Chromium and current system font. The concept's global self-hosted-font migration is a separate reviewed slice; another operating system needs its own approved capture environment.
