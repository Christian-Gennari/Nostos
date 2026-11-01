# App fonts

Hanken Grotesk (UI) and Newsreader (reading and headings), self-hosted so the
app makes no request to Google (issue #1322) and the service worker caches them
(`/fonts/**`, lazy). Declared with `@font-face` at the top of `src/styles.css`;
the latin faces of both are preloaded in `src/index.html`.

Fontsource variable builds of the Google Fonts releases, latin and latin-ext
(latin-ext only downloads when a page contains such a character):

| Family | Folder | Package | Licence |
| --- | --- | --- | --- |
| Hanken Grotesk | `hanken-grotesk/` | `@fontsource-variable/hanken-grotesk@5.3.0` (wght) | SIL OFL 1.1 |
| Newsreader | `newsreader/` | `@fontsource-variable/newsreader@5.3.0` (opsz, wght) | SIL OFL 1.1 |

Each folder carries the package's licence as `OFL.txt`. To update: `npm pack`
the package, copy `files/<family>-{latin,latin-ext}-{wght|opsz}-{normal,italic}.woff2`
and `LICENSE` over the folder.
