# Libron

The EPUB reader's default reading typeface (`Libron` in View settings).

- Upstream: <https://github.com/nicoverbruggen/libron>
- Version: v0.25 (`Libron_Web.zip` release asset, commit `46cf11c`)
- Licence: SIL Open Font License 1.1 — see [`OFL.txt`](./OFL.txt), copied
  unmodified from upstream `LICENSE`.

The four WOFF2 files are the upstream release files, byte for byte. They are
served from `/fonts/libron/` and loaded only inside EPUB contents documents;
Libron is not an application UI font.

To update: replace the four `.woff2` files and `OFL.txt` from a newer upstream
release and bump the version above.
