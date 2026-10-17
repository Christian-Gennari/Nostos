# Studio page fonts

The typefaces a writer can choose for the Studio page (issue #1041). They are
served from `/fonts/studio/<family>/` so the page never depends on an external
font host: Nostos is self-hosted, runs offline and as an Android app.

Each family is declared with `@font-face` in
`src/app/ui/markdown-editor/markdown-editor.component.css`. A browser only
downloads a face when text is set in it, so a family costs nothing until it is
chosen. The service worker caches `/fonts/**` lazily, on first use.

All files are the Fontsource builds of the Google Fonts releases, the **latin**
subset (which covers Swedish å ä ö and the rest of Latin-1), byte for byte:
regular, italic and semibold (bold where the family has no 600).

| Family | Folder | Fontsource package | Licence |
| --- | --- | --- | --- |
| Alegreya | `alegreya/` | `@fontsource/alegreya@5.3.0` | SIL OFL 1.1 |
| Crimson Pro | `crimson-pro/` | `@fontsource/crimson-pro@5.3.0` | SIL OFL 1.1 |
| EB Garamond | `eb-garamond/` | `@fontsource/eb-garamond@5.3.0` | SIL OFL 1.1 |
| Inter | `inter/` | `@fontsource/inter@5.3.0` | SIL OFL 1.1 |
| Libre Baskerville | `libre-baskerville/` | `@fontsource/libre-baskerville@5.3.0` | SIL OFL 1.1 |
| Literata | `literata/` | `@fontsource/literata@5.3.0` | SIL OFL 1.1 |
| Lora | `lora/` | `@fontsource/lora@5.3.0` | SIL OFL 1.1 |
| OpenDyslexic | `opendyslexic/` | `@fontsource/opendyslexic@5.3.0` | SIL OFL 1.1 |
| PT Serif | `pt-serif/` | `@fontsource/pt-serif@5.3.0` | SIL OFL 1.1 |
| Source Serif 4 | `source-serif-4/` | `@fontsource/source-serif-4@5.3.1` | SIL OFL 1.1 |
| Special Elite | `special-elite/` | `@fontsource/special-elite@5.3.0` | Apache 2.0 |

Each folder carries the package's `LICENSE` unmodified. Newsreader and Hanken
Grotesk, also offered, are the app's own faces and load with the app.

To update a family: `npm pack @fontsource/<family>`, copy the
`files/<family>-latin-{400-normal,400-italic,600-normal}.woff2` (or `700-normal`)
and `LICENSE` over the folder, and bump the version above.
