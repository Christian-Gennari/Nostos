# Nostos frontend

The Angular 22 app shared by SelfHosted Nostos and Nostos Cloud: the library, readers, Brain, Writing Studio, Ask Nostos and Settings. For what Nostos is and how to run the whole product, see the [root README](../README.md).

## Run it

From the repository root, `npm start` runs the backend and this app together. The dev server is at http://localhost:4200 and proxies `/api` and `/opds` to the backend on port 5099 (`src/proxy.conf.json`).

To run only the frontend against a backend that is already running:

```bash
npm install
npm start          # ng serve
```

## Checks

```bash
npm run check      # CSS integrity, theme-token graph, design rules (fast, always run)
npm test           # unit tests (run it bare; see AGENTS.md)
npm run build      # production build
```

## See a change without deploying it

```bash
npm run build
npm run preview -- shots --out <dir> --paths /library,/settings            # SelfHosted
npm run preview -- shots --out <dir> --paths /settings --cloud             # Cloud (simulated)
```

## Conventions

- Standalone components and Signals.
- One icon path: `<nostos-icon>` and `appIconButton`, backed by Phosphor. Never import an icon package directly.
- Colour tokens live in `src/styles.css`; every `:root` colour needs its dark-theme counterpart.

The working rules for this repository, including branches, verification and visual evidence, are in [AGENTS.md](../AGENTS.md).
