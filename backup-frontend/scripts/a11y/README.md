# Accessibility audit (Chromium + axe-core)

`audit.mjs` logs in to a running app, loads dashboard, jobs, wizard (every step up to the review, nothing is saved), artifacts and agent detail at a desktop (1366x900, mouse) and a mobile (390x844, touch) viewport, then for each page state:

- runs axe-core (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`, `best-practice`) and keeps violations and `incomplete` results;
- measures every interactive element (links, buttons, form controls, `summary`, ARIA widgets) and classifies it.

Extra states: dashboard with all `<details>` open (technical drawers), mobile dashboard with the navigation drawer open.

## Run

```sh
bun run dev                      # or any running build; the dev server proxies /api (BACKUP_API_PROXY_TARGET, BACKUP_API_PROXY_INSECURE=true)
A11Y_BASE_URL=http://localhost:5173 A11Y_STATE=/path/to/state.json bun scripts/a11y/audit.mjs
```

| Variable | Default | Meaning |
| --- | --- | --- |
| `A11Y_BASE_URL` | `http://localhost:5173` | App under test |
| `A11Y_STATE` | `<repo>/docs/test-environment/state.json` | Lab state file; `a.login`, `a.password`, `a.tenantId` are used (read only) |
| `A11Y_ROLE` | `a` | Key in the state file |
| `A11Y_OUT` | `scripts/a11y/results/audit.json` | JSON output |
| `A11Y_CHROMIUM` | `/usr/bin/chromium` | Browser binary |

Exit code is 1 when any serious or critical axe finding or any undersized target without exemption remains.

## Target-size rule

Design target is 44x44 CSS px. A target below that is `exempt` only for one of these written reasons (each element carries its reason in the JSON):

- `inline`: link inside a sentence of prose (WCAG 2.5.8 inline exception);
- `disabled`: inactive control;
- `sr-only`: 1 px skip link, shown only on keyboard focus;
- `fine-pointer`: mouse pointer and at least 24x24 px, the WCAG 2.5.8 AA minimum. On touch (`pointer: coarse`) there is no such exemption: `pointer-coarse:` variants in `button-variants.ts` and `rowLinkClass` in `form-styles.ts` raise these to 44 px;
- `spacing`: under 24 px but the 24 px circle around it touches no other target (fine pointer only).

A checkbox/radio counts with its associated label box, because the label activates it.

## Limits

- `incomplete` axe results (for example contrast on overlapping text) are recorded but are not failures.
- axe does not emulate `forced-colors`; contrast under forced colors is checked by `contrast-audit.mjs`.
- `heading-order` (moderate) in the mobile card lists is reported, not gated.
