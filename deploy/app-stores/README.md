# App-store packages

Ready-to-submit packages that list SelfHosted Nostos in self-hosting app stores. Each folder is laid out the way its store expects, so a submission is a copy of that folder.

| Store | Folder | Image reference | Submitted as |
| --- | --- | --- | --- |
| Unraid Community Applications | `unraid/` | `:latest` | a templates repository, added at ca.unraid.net |
| CasaOS / ZimaOS | `casaos/Nostos/` | release tag | a PR to IceWhaleTech/CasaOS-AppStore |
| Umbrel | `umbrel/nostos/` | release tag + index digest | a PR to getumbrel/umbrel-apps |
| Runtipi | `runtipi/apps/nostos/` | release tag | a Nostos-owned app store repository |
| selfh.st icons | `selfhst/` | — | a "New Icon Request" discussion |

Nothing here is built, tested or copied into the image, so CI treats this folder as documentation (`scripts/only-docs-changed.sh`).

## What every package encodes

These facts come from the `Dockerfile` and the self-hosting docs. If one of them changes, update every package (see [When the image changes](#when-the-image-changes)).

- **Image:** `ghcr.io/christian-gennari/nostos`, published for `linux/amd64` and `linux/arm64`.
- **Port:** the container listens on `8080`. The host port is `5099`, as in `compose.yml`.
- **Data:** one volume, `/data`. It holds `nostos.db`, `books/`, `backups/` and the key ring in `keys/`. Users must back it up, and it must be on local storage (SQLite locking).
- **Optional media:** a read-only folder at `/external/media`, used from **Settings → This server → External media**.
- **User:** the image runs as UID `1654`, with its temp folder `/tmp/nostos` owned by that user. A host folder created by root or by another user is not writable by it, and Nostos then exits with `Access to the path '/data/transfers' is denied`. Each package solves this the way its store prefers:
  - Unraid creates missing host paths owned by `99:100`, so the template runs the container with `--user 99:100` and `TMPDIR=/tmp`.
  - Umbrel creates app data owned by `1000:1000`, so the service runs as `user: "1000:1000"` with `TMPDIR=/tmp`.
  - CasaOS and Runtipi create the folder as root. A one-shot `nostos-init` service (the same image, as root) runs `chown 1654:1654 /data` and exits, and Nostos starts after it completes. It changes only the folder itself, not the files in it.
- **No login:** SelfHosted Nostos has no accounts. Every description says so and tells users to keep it on a private network. On Umbrel the app sits behind the Umbrel login, except the read-only OPDS catalog (`/opds/*`), which e-reader apps must reach without a browser cookie.
- **Memory:** at least 512 MB; 1 GB is comfortable (measured in #1224).
- **Health:** `/health/live` (process) and `/health/ready` (ready to serve). The image has no `curl` or `wget`, so the packages do not define a container health check.

## Scripts

```bash
python3 deploy/app-stores/check.py                    # all packages agree with the Dockerfile and each other
python3 deploy/app-stores/update-image.py v2026.10.25 # point the packages at a release
python3 deploy/app-stores/make-assets.py              # rebuild icons and screenshots from the brand kit
```

- `check.py` parses every XML, YAML and JSON file, runs `docker compose config -q` on the CasaOS and Runtipi compose files, and checks each store's rules that can be checked offline. It needs PyYAML.
- `update-image.py` asks ghcr.io for the release's index digest and refuses a release that lacks `linux/amd64` or `linux/arm64`. It then updates the image tag, version and date in the CasaOS, Umbrel and Runtipi packages, and the digest in the Umbrel package. The Unraid template follows `latest` and needs no change.
- `make-assets.py` scales the brand kit's PNGs and the README screenshots. It needs Pillow with WebP support.

## Before the first submission

The packages pin `v2026.10.25`, the first release published for both amd64 and arm64 (#1222, #1224). Re-run the update for a newer release before submitting if one exists:

```bash
gh release list -R Christian-Gennari/Nostos -L 1
python3 deploy/app-stores/update-image.py <that tag>     # fails if it is not amd64 + arm64
python3 deploy/app-stores/check.py
```

Commit the result, then submit. The Unraid template and the selfh.st icons do not depend on the release.

## When the image changes

| Change | Update |
| --- | --- |
| New release | `update-image.py <tag>`, then `check.py`. Open an update PR in CasaOS and Umbrel; push the Runtipi store. |
| Container port (`EXPOSE`, `ASPNETCORE_URLS`) | Unraid `Config Target` and `WebUI`; CasaOS `ports.target` and `x-casaos.ports`; Umbrel `APP_PORT`; Runtipi `internal_port`. |
| Host port 5099 | Unraid `Default`; CasaOS `published` and `port_map`; Umbrel `port` (must stay unique in umbrel-apps); Runtipi `port` (unique in the store). |
| Data path `/data` or its layout | Every volume target, the `nostos-init` command, and the descriptions that list what the folder holds. |
| New environment variable users must set | Unraid `Config Type="Variable"`; CasaOS `environment` plus `x-casaos.envs`; Umbrel `environment` (and `environment:` in the manifest to make it user-editable); Runtipi `environment` plus `form_fields` in `config.json`. |
| Image user (UID 1654) or temp folder | The `chown` in both `nostos-init` services, and `TMPDIR` in Unraid and Umbrel. |
| Brand mark or README screenshots | `make-assets.py`; the CasaOS and Unraid image URLs point at files in this repository and the store repositories. |
| Supported architectures | CasaOS `architectures`, Runtipi `supported_architectures`, Unraid `Requires`. Umbrel requires amd64 and arm64. |

`check.py` reads the port and volume from the `Dockerfile`, so it fails until the packages match.

## Unraid Community Applications

**Package:** `unraid/ca_profile.xml`, `unraid/templates/nostos.xml`, `unraid/nostos-icon.png` (512 × 512).

Community Applications needs `ca_profile.xml` in the **root** of a public repository with an OSI-approved licence, and scans that repository for templates. Putting it in the root of this repository would make CA scan the whole app. The template therefore lives in a small repository, **`Christian-Gennari/nostos-unraid`**, whose root is a copy of `unraid/`. This folder stays the source of truth. The template's `TemplateURL` already points at that repository; the icon and screenshots are served from this repository's `main`.

The template uses `:latest`: Unraid's update check follows the tag the user installed, and `latest` only moves after the publish workflow has verified both architectures.

Submit:

1. Create the public repository `Christian-Gennari/nostos-unraid`.
2. Copy `deploy/app-stores/unraid/*` (`ca_profile.xml`, `templates/`, `nostos-icon.png`) and this repository's `LICENSE` (AGPL-3.0, OSI-approved) into its root, and push to `main`.
3. Check that `https://raw.githubusercontent.com/Christian-Gennari/nostos-unraid/main/templates/nostos.xml` and the icon URL in the template open.
4. Sign in at <https://ca.unraid.net/submit/new>, enter the repository URL, run **Validate** and **Scan**, fix anything reported, and confirm.
5. Optional: open a support thread in the Unraid forum and add it as `<Support>` and `<Forum>`; GitHub issues are accepted as support.

## CasaOS and ZimaOS

**Package:** `casaos/Nostos/` with `docker-compose.yml` (Compose plus the top-level `x-casaos` block), `icon.svg`, `icon.png` (256), `thumbnail.png` (1568 × 882, the size the store uses), and `screenshot-1.jpg` to `screenshot-5.jpg` (1440 × 900). One store repository serves both CasaOS and ZimaOS. The app id is `page.nostos.nostos`; data goes to `/DATA/AppData/nostos/data` and media to `/DATA/Media/Books`.

Validated here: the store's `validate_compose.py` passes. Its build (`build_appstore.py` from `IceWhaleTech/build-appstore-action` v1.1.2) rejects `v2026.10.18` because the image has no arm64 build. With a multi-arch tag it builds the app, with a compose file for each architecture. The `nostos-init` pattern was run locally on a root-created folder: it starts, takes an upload, and keeps it across a restart.

Submit:

1. Fork <https://github.com/IceWhaleTech/CasaOS-AppStore> and copy `casaos/Nostos/` to `Apps/Nostos/`.
2. Run `./scripts/build_dist.sh` in the fork. Expect `OK page.nostos.nostos` and `dist/apps/page.nostos.nostos/`.
3. Open a PR titled "Add Nostos". Say it is a new app, what it is, and how it was validated (the steps above).

## Umbrel

**Package:** `umbrel/nostos/` with `umbrel-app.yml`, `docker-compose.yml` (an `app_proxy` service and one `web` service) and `data/library/.gitkeep`. There is no `exports.sh`: the app needs no secrets or computed values. Umbrel hosts the icon and gallery itself, so the package has none, and `gallery` is empty as their rules ask. Port `5099` is not used by any other Umbrel app.

Umbrel requires every image to be multi-arch and pinned as `tag@sha256:<index digest>`, published without credentials, with no Docker socket, privileged mode or host networking. The package meets these. Their linter (`node .tools/lint-apps.mjs nostos --check-images`, Node 22+) rejected the amd64-only `v2026.10.18` index with exactly that one error; with a multi-arch digest it passes.

Submit:

1. Fork <https://github.com/getumbrel/umbrel-apps>, copy `umbrel/nostos/` to `nostos/`, and run `npm ci && npm run lint:apps -- nostos --check-images`.
2. Install it on an Umbrel device or umbrelOS test VM, as their `umbrel-test-app` skill describes: install, open, add a book, restart, check the book is still there. Tick what was tested in the PR template.
3. Open the PR titled "Add Nostos", then set `submission:` in `umbrel-app.yml` to the PR URL (it is a placeholder now) and push.

PR body notes for Umbrel's review:

- Upstream: <https://github.com/Christian-Gennari/Nostos> (AGPL-3.0). Image: `ghcr.io/christian-gennari/nostos`, built by the project's own CI.
- No account and no default credentials; the app is protected by the Umbrel login.
- `PROXY_AUTH_WHITELIST: "/opds/*"`: OPDS reader apps (KOReader and others) cannot send the Umbrel cookie. The catalog is read-only.
- Runs as `1000:1000` with `TMPDIR=/tmp`; the image supports any UID.
- Logo: <https://raw.githubusercontent.com/Christian-Gennari/Nostos/main/_brand-assets/04_App_Icon/nostos-app-icon-1024.png>. Screenshots: the five in `docs/screenshots/` listed in the Unraid template.

## Runtipi

**Package:** `runtipi/apps/nostos/` with `config.json`, `docker-compose.yml` (schema version 2, `x-runtipi` metadata), `metadata/description.md` and `metadata/logo.jpg` (512, square). `config.json` validates against the store's `app-info-schema.json`, port `5099` is free in the official store, and the files meet the official store's tests: the main service is named after the id, its image tag contains `version`, and the logo is a JPEG. The compose file was run locally with the init service, as for CasaOS.

The official store (`runtipi/runtipi-appstore`) no longer accepts new apps (its README: "no new applications will be accepted"). Runtipi supports extra app stores from v4.0.0, so Nostos needs its own store repository: any public repository with an `apps/` folder in its root.

Submit:

1. Create the public repository `Christian-Gennari/nostos-runtipi-store` (Runtipi's template: <https://runtipi.io/docs/guides/create-your-own-app-store>) and copy `deploy/app-stores/runtipi/apps/` into its root.
2. Tell users to add it in Runtipi under **Settings → App Stores → Add App Store**, with that URL.
3. Optional: list it in the Runtipi community app stores discussion (<https://github.com/runtipi/runtipi/discussions/categories/app-stores>).

## selfh.st icons and app directory

**Package:** `selfhst/svg/`, `selfhst/png/` and `selfhst/webp/`, each with `nostos`, `nostos-light` and `nostos-dark`, named like the collection (`<reference>`, `<reference>-light`, `<reference>-dark`). The standard icon is the brand mark (forest tile, white arch). The light and dark icons are one-colour knockouts (white and `#1C1B1A`), drawn as a single even-odd path, which is how selfh.st recolours icons. PNG and WebP are 512 × 512; selfh.st makes its AVIF and ICO files from the SVG.

selfh.st does not accept pull requests from outside its organisation. Requests go through discussions.

Submit the icon: open a discussion in the **New Icon Request** category at <https://github.com/selfhst/icons/discussions>, attach the three SVGs (and the PNGs), and use this text:

> **Nostos** — https://nostos.page · https://github.com/Christian-Gennari/Nostos
>
> Nostos is a self-hosted home for your reading life: books, EPUBs, PDFs and audiobooks, with highlights, notes, a topic graph and a writing studio. I'm the developer. Attached are the official mark (`nostos.svg`) and one-colour light and dark versions (`nostos-light.svg`, `nostos-dark.svg`), with 512 px PNGs. Suggested reference: `nostos`. Category: Self-Hosted. Tags: Books, Reading.

Submit the app at <https://selfh.st/submit/>:

- **Name:** Nostos
- **Website:** https://nostos.page
- **Source:** https://github.com/Christian-Gennari/Nostos
- **Licence:** AGPL-3.0
- **Category / tags:** Books, Reading, Notes, Audiobooks
- **Short description:** A self-hosted home for your reading life: one library for books, ebooks and audiobooks, with notes, a topic graph and a writing studio.
- **Description:** Nostos keeps reading, thinking and writing in one place. Collect physical books, EPUBs, PDFs and audiobooks; read or listen in the app; keep highlights, notes and passages next to the page they came from; connect ideas across books; and turn them into your own writing. It runs as one Docker container (amd64 and arm64) with SQLite and FFmpeg included, needs no account, and offers OPDS for e-readers and a portable export. A hosted version, Nostos Cloud, is also available.
- **Docker image:** ghcr.io/christian-gennari/nostos
- **Icon:** the selfh.st icon reference once it is published (`nostos`).
