# Read aloud

Nostos can read an EPUB or a PDF to you from the page it is open on. It marks
the sentence being spoken, turns the page by itself and carries on into the
next chapter.

Open a book, tap the page to bring up the toolbar, and press the speaker button.
A small bar appears with previous sentence, play/pause, next sentence, speed and
stop. It stays on screen while the rest of the toolbar is hidden.

## Two kinds of voice

| Voice | Where it runs | Needs | Works with the screen locked |
| --- | --- | --- | --- |
| Device voice | The phone, tablet or computer you read on | Nothing | Usually not. Nostos keeps the screen awake while it reads |
| Natural voice | A speech server | A speech server connected to Nostos | It is built to: it plays as ordinary audio, like an audiobook. Not yet confirmed on every phone |

The device voice is always there and is what Nostos uses until a natural voice
is connected. How it sounds depends on the device.

When a natural voice is connected, Nostos uses it for books in a language the
voice can read and the device voice for every other book. If the natural voice
fails in the middle of a book, reading carries on with the device voice and a
short message says why.

The voice is chosen in the reader, under **View settings → Read aloud voice**.
The choice is remembered on that device.

## Connect a natural voice (SelfHosted)

Nostos talks to any speech server that offers the OpenAI speech route
(`POST /v1/audio/speech`). This guide uses [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M),
an open model, served by [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI).
It runs on your own machine, needs no account or key, and no text leaves it.

### 1. Run a speech server

With Docker Compose, add it next to Nostos:

```yaml
services:
  nostos:
    image: ghcr.io/christian-gennari/nostos:latest
    # ...your existing nostos service, unchanged

  kokoro:
    # Pin a release tag from the Kokoro-FastAPI project instead of `latest`.
    image: ghcr.io/remsky/kokoro-fastapi-cpu:latest
    restart: unless-stopped
```

Then run `docker compose up -d`. The speech server does not need a published
port: only Nostos calls it. Without Docker, start Kokoro-FastAPI any other way
and note its address, for example `http://localhost:8880`.

### 2. Connect it in Settings

Open **Settings → AI connections → Read aloud voice**:

1. Switch on **Enable the natural voice**.
2. Set **Endpoint** to the speech server's address: `http://kokoro:8880` in the
   Compose example above.
3. Leave **Model** as `kokoro` and **API key** empty for a local server.
4. Press **Test connection**. It asks the server to say one word and reports
   whether audio came back.
5. Press **Save**.

Then open a book and press the speaker button. The first sentence takes a
moment while the server renders it; the play button pulses until sound starts.

### Or connect it in configuration

The same values can be set as environment variables (or in
`appsettings.json`), which suits a server set up from a script. Anything saved
in Settings takes precedence.

```bash
export ReadAloud__Enabled=true
export ReadAloud__BaseUrl=http://localhost:8880
```

## Configuration settings

All of these go in the `ReadAloud` section of `appsettings.json`, or as
`ReadAloud__Name` environment variables. The first four can also be set in
Settings, which takes precedence.

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `false` | Turns the natural voice on |
| `BaseUrl` | not set | Address of the speech server. `http://host:8880` and `http://host:8880/v1` both work |
| `Model` | `kokoro` | Model name sent to the speech server |
| `ApiKeyEnvironmentVariable` | `NOSTOS_READ_ALOUD_TOKEN` | Name of the environment variable holding a bearer token. Leave it unset for a local server |
| `MaxCharacters` | `1200` | Longest text accepted in one request. The reader sends one sentence at a time |
| `RequestTimeoutSeconds` | `60` | How long to wait for the speech server |
| `CacheDirectory` | a folder under the system temp directory | Where rendered sentences are kept |
| `CacheMaxBytes` | `536870912` (512 MiB) | Size limit of that folder. `0` turns the cache off |

## Languages

The natural voice reads books in these languages. A book declares its language
in its own metadata; a book in any other language, or one that declares none,
is read with the device voice.

| Language | Voices |
| --- | --- |
| English (US) | Heart, Bella, Michael, Fenrir |
| English (UK) | Emma, Isabella, George, Fable |
| Spanish | Dora, Alex |
| French | Siwis |
| Hindi | Alpha, Omega |
| Italian | Sara, Nicola |
| Japanese | Alpha, Kumo |
| Portuguese (Brazil) | Dora, Alex |
| Chinese (Mandarin) | Xiaoxiao, Yunxi |

## What to expect from your hardware

Kokoro runs on a CPU. On a 12-core desktop it rendered speech about three times
faster than it is spoken, measured with the model's ONNX build. Nostos fetches
the next two sentences while the current one plays, so reading is continuous on
a machine that renders faster than real time. On a slower machine there are
pauses between sentences.

Rendered sentences are cached on the server, so going back a page or reading a
chapter again costs nothing the second time.

## PDFs

A PDF has no sentences, only positioned pieces of text, so Nostos rebuilds
them. It leaves out page numbers and running headers in the margins, rejoins a
word hyphenated at the end of a line, skips raised note numbers, and says a
sentence that runs over a page break in one go.

- A scanned PDF with no text layer has nothing to read, and the speaker button
  is not shown for it.
- A PDF does not say what language it is in. Nostos uses the language recorded
  for the book in your library, so set it there if the wrong voice reads.
- Pages with several columns, tables or formulas are read in the order the PDF
  stores them, which is not always the order you would read them in.

## Limits

- The natural voice needs to know the book's language: from the EPUB's own
  metadata, or from the book's language in your library for a PDF.
- Changing speed is instant: it changes playback, not the rendering.
- With the device voice, most phones stop reading when the screen locks. That
  is a limit of the browser's speech engine, which is why Nostos keeps the
  screen awake while it reads.
