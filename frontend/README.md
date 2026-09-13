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

### Initial bundle budget

The production `initial` bundle has a 3 MB warning budget and a 5,121 kB error
budget in `angular.json`. Release candidates should stay at or below 5,000 kB
so normal growth does not immediately block promotion.

Measure the optimized bundle from this directory and open the emitted esbuild
metadata with a bundle analyzer:

```bash
npx ng build frontend --configuration production --stats-json
npx --yes esbuild-visualizer --metadata dist/frontend/stats.json --filename bundle-analysis.html
```

Route-specific components and heavy libraries must use `@defer`,
`loadComponent`, `loadChildren`, or `import()` so they are absent from the
initial graph. A budget increase requires a dated entry in this section that
names the feature or dependency that grew and explains why it must load before
the first board render.

- 2026-09-13: kept the 5,121 kB error budget unchanged. Since `cf1997665`, the
  initial bundle grew from 4,986.47 kB to 5,250.74 kB, led by retention,
  execution-host, task-detail, project-detail, studio-shell, and watcher
  additions. Splitting task-detail runtime state from its lazy component entry
  moved 905.91 kB out of the initial graph, reducing it to 4,344.83 kB.

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
