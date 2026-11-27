// @ts-check
const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: ['app', 'mockup'], style: 'kebab-case' },
      ],
      '@angular-eslint/prefer-on-push-component-change-detection': 'error',
      '@angular-eslint/computed-must-return': 'error',
      '@angular-eslint/no-implicit-take-until-destroyed': 'error',
      '@angular-eslint/no-async-lifecycle-method': 'error',
      '@angular-eslint/no-empty-lifecycle-method': 'error',
      '@angular-eslint/no-input-rename': 'error',
      '@angular-eslint/no-inputs-metadata-property': 'error',
      '@angular-eslint/no-output-native': 'error',
      '@angular-eslint/no-output-on-prefix': 'error',
      '@angular-eslint/no-output-rename': 'error',
      '@angular-eslint/no-outputs-metadata-property': 'error',
      '@angular-eslint/no-queries-metadata-property': 'error',
      '@angular-eslint/prefer-output-emitter-ref': 'error',
      '@angular-eslint/prefer-output-readonly': 'error',
      '@angular-eslint/prefer-standalone': 'error',
      // Cycle 11d: hard-floor the 3-file shape established in 11a/b.
      // Inline `template:` and `styles:` blocks bloat .ts files, defeat
      // per-file-type grep, and shut Prettier/template-aware lint out
      // of the markup. Allow zero lines of either; tiny one-line stubs
      // can stay inline by rare exception (raise the limit locally if
      // ever needed).
      '@angular-eslint/component-max-inline-declarations': [
        'error',
        { template: 0, styles: 0, animations: 0 },
      ],
      // ADR-0034 / Cycle 9h+10j: cross-feature imports must use the
      // feature barrel (./features/<name>), not deep paths. The barrel
      // is each feature's public API; deep imports turn every internal
      // file into an external contract.
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '**/features/*/components/**',
                '**/features/*/services/**',
                '**/features/*/models/**',
                '**/features/*/state/**',
              ],
              message:
                'Cross-feature imports must use the feature barrel (./features/<name>), not deep paths. See frontend/AGENTS.md.',
            },
          ],
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {
      '@angular-eslint/template/conditional-complexity': [
        'error',
        { maxComplexity: 5 },
      ],
      '@angular-eslint/template/eqeqeq': 'error',
      '@angular-eslint/template/no-empty-control-flow': 'error',
      '@angular-eslint/template/no-negated-async': 'error',
      // Design-system rule: literal `style="..."` attributes in templates are
      // a token-bypass — the design-system tokens stop being authoritative the
      // moment a template ships a raw hex / px. Programmatic style bindings
      // (`[style.width.px]="..."`, `[ngStyle]="..."`) are exempt because they
      // are needed for runtime-computed geometry (e.g. the side-sheet width
      // animation). Severity: warning while the codebase migrates; promotion
      // to error is tracked in docs/quality/frontend/style-guide/migration-status.md.
      '@angular-eslint/template/no-inline-styles': [
        'warn',
        { allowNgStyle: true, allowBindToStyle: true },
      ],
    },
  },
  {
    // The barrel-only rule is a guard for cross-feature wiring.
    // Files inside a feature freely use relative paths into their own
    // components/services/models/state — that's not a deep import.
    files: ['**/features/*/**/*.ts'],
    rules: {
      'no-restricted-imports': 'off',
    },
  },
  {
    // Spec + e2e fixture files often need to reach into private
    // implementation details. Don't punish them.
    files: ['**/*.spec.ts', '**/e2e/**/*.ts'],
    rules: {
      'no-restricted-imports': 'off',
    },
  },
  {
    files: ['projects/studio-ui/**/*.ts'],
    rules: {
      '@angular-eslint/component-selector': ['error', { type: 'element', prefix: ['sui', 'app'], style: 'kebab-case' }],
      '@angular-eslint/directive-selector': ['error', { type: 'attribute', prefix: ['sui', 'app', 'cac'], style: 'camelCase' }],
      'no-restricted-imports': ['error', { patterns: [
        { group: ['**/src/app/**', '**/app/services/**', 'coding-agent-chat', 'coding-agent-chat/**'], message: 'studio-ui cannot depend on the Studio host. Use a host provider.' },
      ] }],
    },
  },

);
