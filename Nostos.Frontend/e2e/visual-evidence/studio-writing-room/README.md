# Writing Studio, end to end (epic #1036)

The final journey on `feature/studio-writing-room` at 37c69c15 plus docs (#1056). It ran in Chromium against a
fresh synthetic library: Schopenhauer's *The World as Will and Representation*
and *Studies in Pessimism*, plus Nietzsche's *Thus Spoke Zarathustra*, all public domain.
`journey.cjs` is the script. Run it as `node journey.cjs <Nostos.Frontend> <frontend url> <api url> <out dir> 1440x900:light`.

Steps (file prefix):

1. `1-blank-page`: the Studio opens on a blank page with the caret in it.
2. `2-writing`: the first words create a writing named from its first line.
3. `3a-palette`, `3b-quote`: `@schopenhauer will` finds the quote, and Enter inserts it with `— Arthur Schopenhauer, *The World as Will and Representation*`.
4. `4a-passage-open`, `4b-passage-quoted`: `@vanity` opens the passage "On the Vanity of Existence". Two of its sentences are selected and quoted, not the whole passage.
5. `5-theme-font`: the Studio theme is set to Sepia and the typeface to EB Garamond.
6. `6a-sounds-on`, `6b-typing-with-sounds`: typewriter sounds are turned on, and typing plays them (`AudioBufferSourceNode.start` is counted).
7. `7-after-reload`: after a reload, the same writing reopens with everything shown. The theme, typeface and sounds are kept, and the saved Markdown holds the heading, both quotes with their attributions, and the last line.

Each step was taken at 1440×900 and 390×844 (touch), in light and dark app themes. All four runs report `ALL PASSED` with no page errors.

`cloud-*`: the production build under `npm run preview -- serve --cloud`, showing the blank page and the `@` palette. The preview is a read-only simulation of a Cloud host (every non-GET is refused), so the steps that save cannot run there.

Saved Markdown from the 1440 light run:

```markdown
# Journey 1440-light 1792266785708

What we want is never what we need, and the wanting itself is the trouble.

> Every true act of his will is also at once and without exception a movement of his body.
> 
> — Arthur Schopenhauer, _The World as Will and Representation_

> Time is that in which all things pass away; it is merely the form under which the will to live has revealed to it that its efforts are in vain. It is that agent by which at every moment all things in our hands become as nothing, and lose any real value they possess.
> 
> — Arthur Schopenhauer, _Studies in Pessimism_, “On the Vanity of Existence”

Written to the sound of keys.
```
