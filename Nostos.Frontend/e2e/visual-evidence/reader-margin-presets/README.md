# EPUB margin preset regression

Original-resolution Chromium captures at 1920×900, device scale 1, light theme.
Before uses the merged reader CSS from `463de61d` (after #768); after uses the
margin fix. Screenshots wait until the transient chrome has fully faded out.
Each image follows a real click on Narrow, Normal, or Wide in View settings.
All writes ran against a fresh isolated fixture database.

| Preset | Before | After |
| --- | --- | --- |
| Narrow | [Before](before-narrow.png) | [After](after-narrow.png) |
| Normal | [Before](before-normal.png) | [After](after-normal.png) |
| Wide | [Before](before-wide.png) | [After](after-wide.png) |

The regression test first fails on the merged CSS: the real page box measures
1280px for all three presets. With the fix, measurements are:

| Viewport width | Narrow | Normal | Wide |
| --- | ---: | ---: | ---: |
| 390px | 390.00 | 358.81 | 327.63 |
| 1440px | 1280.00 | 1177.63 | 1075.22 |
| 1920px | 1280.00 | 1177.63 | 1075.22 |
| 2560px | 1280.00 | 1177.63 | 1075.22 |

The browser spec checks both themes, engine layout size, actual paragraph
width changes, centred layout, persisted preferences, and restoration after
reload. The maximum spread is applied first; the preset adds 0/4/8% insets
inside that spread. Wider monitors therefore retain the same reading measure.

`e2e/assets/reader-margins.epub` reuses the tiny fixture's EPUB packaging with
12 public-domain paragraphs from chapter I of Bram Stoker's *Dracula*, sourced
from [Project Gutenberg ebook 345](https://www.gutenberg.org/ebooks/345).
