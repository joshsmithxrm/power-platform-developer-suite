import tseslint from 'typescript-eslint';
import importPlugin from 'eslint-plugin-import-x';

export default tseslint.config(
  // ── Global ignores ───────────────────────────────────────────────────
  {
    ignores: [
      'dist/**',
      'out/**',
      'node_modules/**',
      '*.config.*',
      'esbuild.js',
      'vitest.config.ts',
      'playwright.config.ts',
      'scripts/**',
      'dev/**',
      // Monaco files have their own build pipeline and no tsconfig
      'src/panels/monaco-entry.ts',
      'src/panels/monaco-worker.ts',
    ],
  },

  // ── Base: typescript-eslint recommended + type-checked ───────────────
  ...tseslint.configs.recommendedTypeChecked,

  // ── Extension host TypeScript (src/**/*.ts, excluding tests & webview) ──
  {
    files: ['src/**/*.ts'],
    ignores: [
      'src/__tests__/**',
      'src/**/\u005F\u005Ftests\u005F\u005F/**',
      'src/panels/webview/**',
    ],
    languageOptions: {
      parserOptions: {
        project: './tsconfig.json',
      },
    },
    plugins: {
      'import-x': importPlugin,
    },
    rules: {
      // HIGH PRIORITY — catch real bugs
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/no-misused-promises': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unsafe-return': 'error',
      '@typescript-eslint/no-unsafe-assignment': 'error',
      '@typescript-eslint/no-unsafe-member-access': 'error',
      '@typescript-eslint/no-unsafe-call': 'error',
      '@typescript-eslint/no-unsafe-argument': 'error',
      // Disabled: too many false positives with Record<string, unknown> patterns from Dataverse records
      '@typescript-eslint/no-base-to-string': 'off',
      'no-console': 'error',

      // MEDIUM PRIORITY — code quality
      '@typescript-eslint/explicit-function-return-type': ['error', {
        allowExpressions: false,
        allowTypedFunctionExpressions: true,
        allowHigherOrderFunctions: true,
        allowDirectConstAssertionInArrowFunctions: true,
      }],
      'import-x/order': ['error', {
        groups: ['builtin', 'external', 'internal', 'parent', 'sibling', 'index'],
        'newlines-between': 'always',
      }],
      '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
        caughtErrorsIgnorePattern: '^_',
      }],

      // COMPLEXITY GATES
      'complexity': ['warn', 15],
      'max-lines': ['warn', { max: 500, skipBlankLines: true, skipComments: true }],
      'max-lines-per-function': ['warn', { max: 100, skipBlankLines: true, skipComments: true }],
      'max-depth': ['warn', 4],
      'max-nested-callbacks': ['warn', 3],
    },
  },

  // ── Test files — relaxed rules ───────────────────────────────────────
  {
    files: ['src/__tests__/**/*.ts', 'src/**/\u005F\u005Ftests\u005F\u005F/**/*.ts'],
    languageOptions: {
      parserOptions: {
        project: './tsconfig.test.json',
      },
    },
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
      '@typescript-eslint/no-unsafe-return': 'off',
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-unsafe-member-access': 'off',
      '@typescript-eslint/no-unsafe-call': 'off',
      '@typescript-eslint/no-unsafe-argument': 'off',
      '@typescript-eslint/require-await': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
      '@typescript-eslint/no-base-to-string': 'off',
      'max-nested-callbacks': 'off',
      'max-lines-per-function': 'off',
      'max-lines': 'off',
      'complexity': 'off',
      '@typescript-eslint/explicit-function-return-type': 'off',
    },
  },

  // ── Panel files — raised limits ──────────────────────────────────────
  {
    files: ['src/panels/*Panel*.ts'],
    rules: {
      'max-lines': ['warn', { max: 700 }],
      'max-lines-per-function': ['warn', { max: 150 }],
      'complexity': ['warn', 20],
    },
  },

  // ── Composition root — no size limits ────────────────────────────────
  {
    files: ['src/extension.ts'],
    rules: {
      'max-lines': 'off',
      'max-lines-per-function': 'off',
    },
  },

  // ── Webview TypeScript (browser context) ─────────────────────────────
  {
    files: ['src/panels/webview/**/*.ts'],
    languageOptions: {
      parserOptions: {
        project: './tsconfig.webview.json',
      },
    },
    rules: {
      'no-console': 'off',
      '@typescript-eslint/explicit-function-return-type': 'off',
      '@typescript-eslint/no-base-to-string': 'off',
      'no-restricted-imports': ['error', {
        patterns: [{
          group: ['vscode', 'child_process', 'fs', 'path', 'os'],
          message: 'Cannot import Node.js modules in browser webview context.',
        }],
      }],
    },
  },

  // ── Webview shared message-types — allow host import ─────────────────
  {
    files: ['src/panels/webview/shared/message-types.ts'],
    rules: {
      'no-restricted-imports': 'off',
    },
  },
);
