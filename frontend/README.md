# Frontend

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 21.2.7.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

### Production bundle budget

The production `initial` bundle warns at 3 MB and fails at 5121 kB, as
configured in `angular.json`. Keep routine builds at or below 5000 kB so the
release gate retains at least 120 kB of headroom. Measure the optimized graph
from this directory:

```bash
npm run build -- --configuration production --stats-json
npx --yes esbuild-visualizer --metadata dist/frontend/stats.json \
  --filename bundle-analysis.html --template treemap
```

Route-only panels, dialogs, editors, renderers, and their libraries must enter
through `@defer`, `loadComponent`, `loadChildren`, or `import()` instead of the
board's initial dependency graph. A proposed increase to either initial-bundle
threshold must add a dated entry below that names the code or dependency that
grew, explains why it cannot be loaded on demand, and records the measured
before and after totals.

| Date | Budget change | Reason |
|---|---|---|
| 2026-08-10 | Hard limit increased from 5 MB to 5121 kB | Historical 1 kB release allowance; the originating change did not record a bundle attribution. |

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
