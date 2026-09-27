# Frontend — Routing & Navigation

## Route Table

| Path             | Component              | Lazy | Notes                                         |
| ---------------- | ---------------------- | ---- | --------------------------------------------- |
| `""`             | redirect → `"library"` | —    | Library is the app home                       |
| `"read/:id"`     | `ReaderShell`          | Yes  | Full-screen reader (outside workspace layout) |
| `""` (parent)    | `WorkspaceLayout`      | No   | Shell with router-outlet + dock bar           |
| `"library"`      | `Library`              | Yes  | Book grid / list                              |
| `"second-brain"` | `SecondBrain`          | Yes  | Concept explorer                              |
| `"studio"`       | `WritingStudio`        | Yes  | Writing environment                           |
| `"library/:id"`  | `BookDetail`           | Yes  | Single book detail                            |
| `"**"`           | redirect → `"library"` | —    | Catch-all wildcard                            |

**File:** `src/app/app.routes.ts`

## Workspace Layout

`WorkspaceLayout` is a wrapper shell that provides:

- `<router-outlet>` for child page views
- `<app-app-dock>` fixed bottom navigation bar

All workspace routes (`library`, `second-brain`, `studio`, `library/:id`) render inside this shell. The reader route (`read/:id`) renders **outside** the workspace layout for a distraction-free experience.

## Scroll Position Restoration

The router is configured with `scrollPositionRestoration: 'enabled'` (via `withRouterConfig()` in `app.config.ts`). Angular automatically restores the scroll position when navigating back to a previously visited page.

## Navigation History (Deep-Link Memory)

`NavigationHistoryService` tracks the last visited URL within each section prefix (`/library`, `/second-brain`, `/studio`).

The `AppDockComponent` uses this to restore context:

- Click **Library** while on Studio → navigates to `/library/some-book-id` (last viewed book) instead of always `/library`.
- Click the **same** dock item while already in that section → resets to the section root.

## Lazy Loading

All leaf routes use `loadComponent: () => import(...)` for code-splitting. Each page lands in its own chunk.

## Service worker (PWA) navigation policy

The production build registers the Angular service worker (`ngsw-config.json`, `provideServiceWorker` in `app.config.ts`). The worker answers **navigations** from its app-shell cache, and by default "navigation" means *any dotless path* (`/**` minus paths whose last segment contains `.` or `__`). That silently swallowed backend namespaces: a navigation to `/api/books/{id}/file/download` (the book-detail **Download** button, `window.open`) or `/api/backup/download/{id}` never reached the backend — the worker returned cached `index.html`, which booted the app and the `**` route redirected to `library`.

`ngsw-config.json` therefore declares `navigationUrls` explicitly: the Angular defaults plus `!/api`, `!/opds`, `!/mcp` (bare path **and** `/**`). This mirrors the backend rule that the SPA shell never answers those namespaces (`Program.cs`).

- Adding a backend namespace under a new root path means adding an exclusion pair here too.
- `Mcp:Path` is configurable at runtime, but this list is baked into the build: a deployment that moves MCP off `/mcp` must add that path here as well.
- The policy is guarded by `e2e/service-worker-navigation.spec.ts` (real production build, real backend): API navigations must download the exact file bytes, and client routes must still be answered from the app-shell cache.
