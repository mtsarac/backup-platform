# Accessibility audit (Chromium + axe-core)

`audit.mjs` logs in to a running app, loads dashboard, jobs, wizard (every step up to the review, nothing is saved), artifacts and agent detail at a desktop (1366x900, mouse) and a mobile (390x844, touch) viewport, then for each page state:

- runs axe-core (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`, `best-practice`) and keeps violations and `incomplete` results;
- measures every interactive element (links, buttons, form controls, `summary`, ARIA widgets) and classifies it.

Each state runs in every visual condition (`A11Y_CONDITIONS`, comma list, default all):

| Condition | How it is set |
| --- | --- |
| `light`, `dark` | `localStorage['backup-theme']` plus Chromium `colorScheme` |
| `high-contrast`, `high-contrast-dark` | `localStorage['backup-high-contrast']='1'` (the `.hc` root class), light / dark |
| `forced-colors`, `forced-colors-dark` | Chromium `forcedColors: 'active'` emulation, light / dark scheme |

In forced-colors axe's `color-contrast` rule is disabled (axe does not model system colours; the JSON records it in `axeDisabledRules` / `axeDisabledInForcedColors`). It is the only rule turned off, and only there. Instead `visibleInForcedColors()` measures real computed styles per state: every visible text element must keep a contrast ratio of at least 3 against its composited background, every bordered-control-type element (inputs, selects, textareas) must keep a visible border, and every enabled control must show a non-transparent focus outline when focused. Findings go to `forcedColorsFindings` and fail the run.

`A11Y_SHOTS=<dir>` writes a full-page screenshot per state to `<dir>/<condition>/<viewport>-<page>.png`.

Extra states: dashboard with all `<details>` open (technical drawers), mobile dashboard with the navigation drawer open.

## Resolving axe `incomplete` contrast nodes

axe reports `color-contrast` as `incomplete` when it cannot determine the background (text partly covered by another element). `audit.mjs` never treats those as a pass: for each node it scrolls the text into view, hides all text with a temporary style, takes a real screenshot of the area, and measures the actual rendered pixels behind every text run against that run's own computed colour (inherited opacity included). The threshold is 3:1 for large text (>= 24 px, or >= 18.66 px and bold) and 4.5:1 otherwise. Each node is written to `results[].incompleteResolved` as `{page, condition, viewport, selector, text, fg, bg, ratio, threshold, pass, fontSize, fontWeight, opacity, sampledPixels, stackAtText}` (`bg`/`fg` are the worst pixel). `summary.incompleteContrast` counts them; any node below its threshold, or not resolvable, fails the run.

## Colour maths

`lib/color.cjs` holds the shared parser, alpha compositing and WCAG ratio (loaded into the page by both scripts, `require`d by Node for validation; unit tests in `lib/color.test.ts`). Every channel must be 0..255 and every ratio 1..21: the helpers throw otherwise, and both scripts run `validateDeep()` over the finished results and exit 1 without writing the JSON if any value is out of range. Ratios are truncated to two decimals (never rounded up), like axe-core.

`contrast-audit.mjs` (language switcher only) measures default, mouse-hover and keyboard-focus (Tab) states in six conditions and both languages: text 4.5:1 for label, separator and active label, and the focus outline against the background it is drawn on, 3:1. Under forced-colors axe's contrast result is recorded but not trusted; the verdict comes from the computed styles.

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
| `A11Y_CONDITIONS` | all six | Conditions to run |
| `A11Y_SHOTS` | unset | Screenshot directory |
| `A11Y_OUT` | `scripts/a11y/results/audit.json` | JSON output |
| `A11Y_CHROMIUM` | `/usr/bin/chromium` | Browser binary |

Exit code is 1 when any serious or critical axe finding, any undersized target without exemption, or any forced-colors visibility finding remains. Results carry `condition`, `viewport` and `page` keys; `summary.perCondition` has the counts.

## Target-size rule

Design target is 44x44 CSS px. A target below that is `exempt` only for one of these written reasons (each element carries its reason in the JSON):

- `inline`: link inside a sentence of prose (WCAG 2.5.8 inline exception);
- `disabled`: inactive control;
- `sr-only`: 1 px skip link, shown only on keyboard focus;
- `fine-pointer`: mouse pointer and at least 24x24 px, the WCAG 2.5.8 AA minimum. On touch (`pointer: coarse`) there is no such exemption: `pointer-coarse:` variants in `button-variants.ts` and `rowLinkClass` in `form-styles.ts` raise these to 44 px;
- `spacing`: under 24 px but the 24 px circle around it touches no other target (fine pointer only).

A checkbox/radio counts with its associated label box, because the label activates it.

## Limits

- Other `incomplete` axe results are recorded but are not failures. `color-contrast` incompletes are resolved, see below.
- Forced-colors visibility is a heuristic on computed styles (see above), not a pixel check; the screenshots are the visual evidence. The switcher's forced-colors contrast is also measured by `contrast-audit.mjs`.
- `heading-order` (moderate) in the mobile card lists is reported, not gated.
