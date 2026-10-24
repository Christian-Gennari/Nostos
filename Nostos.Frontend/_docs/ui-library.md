# Frontend — UI Components Library

Reusable components and utilities shared across pages.

## Start here: Nostos UI v1

This is the **implementation reference** for shared UI. The measured visual language
and the reasons behind it live in [`docs/design/design-language.md`](../docs/design/design-language.md);
the CSS/token graph remains the visual source of truth. The live fixture is available
at **`/ui-catalogue`**.

Nostos UI v1 has four layers:

| Layer | Owner | Examples |
| --- | --- | --- |
| **Foundations** | `src/styles.css` token graph | colour, type, radius, elevation, motion, focus, control heights |
| **Primitives** | `src/app/ui/` | Button, IconButton, Input/Textarea, Dropdown, Switch, Chip, Badge |
| **Patterns** | shared composition/recipes | FormField, ModalShell + DialogActions, Toast, segmented visual recipe |
| **Product components** | feature surfaces | BookCard, NoteItem, Reader transport, Studio editor/tree, assistant recording, acquisition workflows |

The boundary is semantic, not visual similarity. Use a primitive when the control is
an ordinary instance of that role. Keep a control product-owned when its interaction,
ARIA contract, geometry, or state model is part of the product surface.

- Use **`appButton`** for ordinary labelled actions such as Save, Cancel, Restore,
  Clear and conventional modal actions.
- Use **`appIconButton`** for icon-only actions while keeping the native button's
  `aria-label`, `title`, `disabled`, click and keyboard semantics at the call site.
- Do **not** force tabs, radio cards, Reader transport/zoom, Studio editor/tree
  controls, assistant recording, drag/drop, acquisition rows or similar product
  interactions through a generic primitive just to remove CSS.
- A shared appearance does not imply shared semantics: segmented controls deliberately
  share a recipe rather than one ARIA-switching component.
- Use **`nostos-empty-state`** for "nothing here" and "could not be loaded", with
  **Try again** as the one retry label; **`nostos-loading-indicator`** for page and
  pane loads; **`nostos-paging-status`** at the end of a paged list.
- Font sizes come from the type steps in `styles.css` where one fits:
  `--text-xs` 0.75rem, `--text-sm` 0.875rem, `--text-md` 1rem, `--text-lg` 1.15rem,
  `--text-xl` 1.35rem, `--text-2xl` 2.1rem. Each is the literal the stylesheets
  already used most at that size; a size between steps stays a literal.

### Copy

- **Sentence case** for every button, label, heading, menu item, tooltip and
  `aria-label`: “Add book”, “Table of contents”, “Delete permanently”. Proper nouns
  keep their capitals: Nostos, Brain, Studio, Ask Nostos, and Library or Settings
  when they name the page.
- **Reading status** wording comes from `src/app/core/reading-status.ts`: Not started,
  Reading, Finished, and To read for the reading list.
- A book without an author shows **Unknown author** (`UNKNOWN_AUTHOR` in `book-format.ts`).
- Placeholders and pending labels end with an ellipsis character, “…”, not three dots.
- Dates go through `nostosDate` (see Pipes); toast wording is under ToastContainerComponent.

---

## ButtonComponent

**Selector:** `button[appButton]`  
**Files:** `src/app/ui/button/`

Canonical Nostos text/action button. The host is the native `<button>`, so callers
keep native `type`, `disabled`, keyboard activation, click handlers and `aria-*`
attributes while the component owns the shared visual action contract.

### Inputs

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `variant` | `'primary' \| 'secondary' \| 'ghost' \| 'danger'` | `'secondary'` | Visual/semantic action role |
| `size` | `'sm' \| 'md'` | `'md'` | Measured compact/default size |
| `busy` | `boolean` | `false` | Emits `aria-busy`; does not override native `disabled` |

Use `appButton` for ordinary labelled actions. Icon-only actions use
`appIconButton`. Tabs, radio-style theme cards and other controls with a
different interaction contract keep their native/product semantics rather than
being forced through the generic Button primitive.

```html
<button appButton variant="primary" type="button">Save</button>
<button appButton variant="secondary" size="sm" type="button">Restore</button>
```


## IconButtonComponent

**Selector:** `button[appIconButton]`  
**Files:** `src/app/ui/icon-button/`

Canonical icon-only action. The host remains the native `<button>`; callers write
`aria-label`, `title`, `disabled`, `type`, click handlers and any real
toggle state directly on that host.

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `icon` | `NostosIconName` | required | Closed-union Nostos/Phosphor glyph name |
| `size` | `'xxs' \| 'xs' \| 'md'` | `'md'` | 24px / 28px / 32px measured box rung |
| `glyphSize` | `number` | `16` | Glyph size in px, independent of box size |
| `weight` | `NostosIconWeight` | `'regular'` | Phosphor glyph weight |
| `tone` | `'default' \| 'danger'` | `'default'` | Hover-ink tone |
| `pressed` | `boolean \| null` | `null` | Emits `aria-pressed` only for a real toggle |

Radius and surface-specific active treatment are intentionally **not** inputs. Reader,
Library, NoteItem and Studio use different measured radii and selected-state contracts;
the primitive owns the stable icon-button box and glyph path, not those product semantics.

```html
<button appIconButton icon="trash" tone="danger" aria-label="Delete note"></button>
<button
  appIconButton
  icon="sidebar-simple"
  [pressed]="sidebarOpen()"
  aria-label="Toggle sidebar"
></button>
```


## SwitchComponent

**Selector:** `label[appSwitch]`  
**Files:** `src/app/ui/switch/`

Canonical 42×24 Nostos switch, extracted from Settings. The component owns the
track, knob, focus treatment and disabled presentation; the projected
`<input type="checkbox">` remains the real control and keeps native checked,
disabled, form, keyboard and `aria-*` semantics.

The checkbox must be the label's first projected child so it sits immediately
before the component-owned track.

```html
<label appSwitch>
  <input
    type="checkbox"
    [checked]="enabled()"
    (change)="setEnabled($event)"
    aria-label="Automatic backup"
  />
</label>
```

Do not add `role="switch"` to replace the checkbox semantics. A product may
choose that role deliberately later, but the primitive itself does not rewrite
native semantics.

## ChipComponent

**Selector:** `button[appChip]`  
**Files:** `src/app/ui/chip/`

Interactive capsule derived from Library's active-filter chip. Use it only when
the capsule itself is a control: filtering, removing a filter/tag, or choosing a
compact option. The host remains a native button; callers own `type`,
`disabled`, accessible naming and, for selectable chips, `aria-pressed`.

```html
<button appChip type="button" aria-label="Remove Philosophy filter">
  Philosophy
  <nostos-icon name="x" [size]="14"></nostos-icon>
</button>

<button appChip type="button" [attr.aria-pressed]="isUnreadSelected()">
  Unread
</button>
```

A normal Save, Cancel, Clear or navigation action is **not** a chip merely
because it is compact. Use `appButton` for ordinary labelled actions.

## BadgeComponent / status

**Selector:** `span[appBadge]`  
**Files:** `src/app/ui/badge/`

Passive metadata/state capsule. Badge adds no role, tab stop, click behaviour or
selection semantics.

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `tone` | `'neutral' \| 'success' \| 'danger'` | `'neutral'` | Passive state tone |
| `dot` | `boolean` | `false` | Settings-style leading status dot |

```html
<span appBadge tone="success">Completed</span>
<span appBadge [dot]="true" [tone]="assistantEnabled() ? 'success' : 'neutral'">
  {{ assistantEnabled() ? 'On' : 'Off' }}
</span>
```

If changing status needs to be announced, put the appropriate live-region
semantics on the status container/call site. Do not make the badge interactive;
use `appChip` when the capsule can be activated.

## Segmented-control visual recipe

Segmented controls are a **pattern, not a semantic component**. The compact
option recipe lives once in `src/styles.css` as `.toggle-opt` and its
`.active`, hover and focus states. The enclosing track follows the existing
Nostos treatment: `--bg-hover`, 3px padding, `--radius-md`, 2px gap and no
border; the selected option uses `--control-active-fill` /
`--control-active-ink`, `--shadow-sm` and a one-pixel
`--border-color` outline.

Keep the surface's real interaction contract:

- tabs keep `role="tablist"` / `role="tab"` / `aria-selected`;
- radio choices keep `role="radiogroup"` / `role="radio"` /
  `aria-checked`;
- pressed-button groups keep native buttons with `aria-pressed`.

Do not create a SegmentedControl mega-component that switches ARIA modes through
inputs. Shared appearance does not imply shared semantics. Large radio cards
such as Settings' current theme choices are also not compact segmented controls
just because they represent a choice.

### Capsule rule

Pill geometry is reserved for roles that read as capsules: chips, badges/status,
switch tracks, deliberate CTA/action pills and progress tracks. Ordinary compact
buttons, fields, cards and row controls keep the app's square/soft-radius
recipes. This prevents the UI kit from turning every small action into a pill.

## Native text form controls

**Selectors:** `input[appInput]`, `textarea[appTextarea]`  
**Files:** `src/app/ui/form-control/` and the canonical control rules in `src/styles.css`

Canonical Nostos styling for ordinary native text controls. The directives do not
wrap or replace the host element, so native `type`, `name`, `required`,
`disabled`, `autocomplete`, `ngModel` and keyboard behaviour remain intact.

Both controls share the same boundary, radius, typography, placeholder, disabled
and focus contract.

```html
<input appInput type="text" name="title" />
<textarea appTextarea controlSize="compact" rows="5" name="description"></textarea>
```

## SearchFieldComponent

**Selector:** `nostos-search-field`
**Files:** `src/app/ui/search-field/`

Every search or filter field (Library, Brain, Map, Studio's reference panel, the
Editions dialog, reader search, the writing hand-off, highlight import, add-book,
the topic pickers). It frames the caller's own input:

```html
<nostos-search-field>
  <input appInput controlSize="compact" type="search"
         placeholder="Search books…" aria-label="Search books"
         [value]="query()" (input)="onSearch($event)" />
</nostos-search-field>
```

- The input stays the caller's: write `type="search"`, a placeholder ending in "…",
  an `aria-label`, and any binding, keyboard handler or combobox ARIA on it as usual.
- A leading magnifying glass, and a clear button once the field has a value. Clear
  empties the input the way typing would (it dispatches `input`, so `ngModel` and
  `(input)` handlers see it), emits `(cleared)` and keeps focus in the field. The
  browser's own clear glyph is hidden.
- `[clearable]="false"` where a Close button already sits beside or above the field
  (reader search, topic pickers), so two X's never mean two things.
- `size="sm"` is the dense rung for panels and dialogs (15px glass, narrower gutter).
- Place it with a class on the host (`class="search-bar-container"`); do not restyle
  the glass or the input's gutter.

---

## DropdownComponent

**Selector:** `app-dropdown`  
**Files:** `src/app/ui/dropdown/`

Canonical Nostos single-choice dropdown. It replaces ordinary raw `<select>`
controls so trigger geometry, popup sizing/positioning, option states, keyboard
navigation and light/dark behaviour have one owner.

The trigger uses the select-only combobox/listbox contract and keeps focus while
the popup is open. Arrow keys move the active option; Home/End jump; Enter/Space
select; Escape closes. The panel uses the browser top layer when available so it
is not clipped by dialogs or constrained surfaces, and falls back to fixed
positioning when Popover is unavailable.

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `options` | `readonly DropdownOption[]` | `[]` | Value/label/options, with optional disabled/icon state |
| `value` | `string \| null` | `null` | Current selected value |
| `placeholder` | `string` | `Select…` | Trigger copy when no option matches |
| `ariaLabel` | `string \| null` | `null` | Accessible name for trigger/listbox |
| `controlId` | `string \| null` | `null` | ID placed on the trigger for FormField labels |
| `controlSize` | `'normal' \| 'compact'` | `'normal'` | Shared control size |
| `align` | `'start' \| 'end'` | `'start'` | Popup alignment relative to trigger |
| `fullWidth` | `boolean` | `false` | Fill the available form-field width |
| `disabled` | `boolean` | `false` | Disables trigger/selection |
| `invalid` | `boolean` | `false` | Applies the canonical invalid boundary/focus state |

**Output:** `valueChange: string`

```html
<app-dropdown
  controlId="book-type"
  ariaLabel="Format"
  [options]="bookTypeOptions"
  [value]="form.type"
  (valueChange)="onTypeChange($event)"
/>
```

Ordinary single-choice controls use `app-dropdown`; new feature templates should
not add raw `<select>` markup. Product-owned controls with genuinely different
semantics remain local rather than being forced through this primitive. In
particular, Book Detail's reading-status listbox and its Edit action menu remain
product-owned, as do Reader playback settings.

## FormFieldComponent

**Selector:** `app-form-field`  
**Files:** `src/app/ui/form-field/`

Reusable presentation frame for an ordinary field's label, required marker,
label note, help text and validation message. The actual input/select/textarea is
projected and remains native.

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `label` | `string` | required | Visible field label |
| `forId` | `string` | required | Native control id used by the label's `for` attribute |
| `required` | `boolean` | `false` | Shows the required marker; the projected native control still owns its real `required` attribute |
| `labelNote` | `string \| null` | `null` | Quiet inline label annotation such as `(Harvard)` |
| `hint` | `string \| null` | `null` | Help text below the control |
| `error` | `string \| null` | `null` | Validation text below the control, rendered as an alert |

`describedBy()` returns the active hint/error ids so callers can connect a
native control with `aria-describedby` when those messages are used.

```html
<app-form-field
  #titleField
  label="Title"
  forId="book-title"
  [required]="true"
  hint="Use the title printed on this edition."
  [error]="titleError"
>
  <input
    id="book-title"
    appInput
    name="title"
    required
    [invalid]="!!titleError"
    [attr.aria-describedby]="titleField.describedBy()"
  />
</app-form-field>
```

Add Book/Edit Book uses `controlSize="compact"` for its ordinary metadata
fields because that modal already had a measured compact field density. File
drop zones, cover acquisition, provider/source search and other special controls
remain product-owned rather than being flattened into FormField.

---

## ModalShell + DialogActions

**Selectors:** `app-modal-shell`, `app-dialog-actions`  
**Files:** `src/app/ui/modal-shell/`, `src/app/ui/dialog-actions/`

Dialog composition has three owners:

1. **ModalShell** owns backdrop/card geometry, scroll regions, sheet-vs-dialog
   responsive behaviour and ARIA-role forwarding.
2. **DialogActions** owns the ordinary footer/inset action-row composition.
3. **Button** owns ordinary action styling.

The caller still owns dialog content, labels, `dialog` vs `alertdialog`, working-area
interactions and any special row controls. ConfirmModal and Editions therefore remain
separate product components even though they can share shell/action primitives.

`DialogActions` supports `variant="footer"` for a pinned/divided form footer and
`variant="inset"` inside an already padded compact dialog. `dialogActionsStart`
projects the uncommon leading action; `stackOnNarrow` stacks the trailing pair when
a compact question needs it.

```html
<app-modal-shell
  [isOpen]="open()"
  variant="dialog"
  dialogRole="alertdialog"
  ariaLabelledBy="confirm-title"
>
  <div shellHeader><h2 id="confirm-title">Delete book?</h2></div>

  <p>This cannot be undone.</p>

  <app-dialog-actions shellActions variant="inset" [stackOnNarrow]="true">
    <button appButton variant="secondary" type="button">Cancel</button>
    <button appButton variant="danger" type="button">Delete</button>
  </app-dialog-actions>
</app-modal-shell>
```

Do not turn ModalShell into a universal working-area component, and do not route
inline row actions through DialogActions merely because they happen to live inside a
dialog.

## Menu choices (`nostosMenuItemRadio`, `nostosMenuGroup`)

**Files:** `src/app/ui/menu/`

A `nostos-menu` (the CDK menu: overlay, arrow keys, Escape, outside click,
focus return) can hold a set of choices as well as actions. A choice is
`button[nostosMenuItemRadio]`: `role="menuitemradio"`, `aria-checked` from its
`checked` input, and a check column every item keeps so the labels line up.
Wrap a set in `[nostosMenuGroup]` (`role="group"`). Do not fake a choice with
`nostosMenuItem` plus `aria-checked`; a `menuitem` cannot be checked.

```html
<nostos-menu aria-label="Voice">
  <div nostosMenuGroup>
    @for (voice of voices(); track voice.id) {
      <button nostosMenuItemRadio [checked]="voice.id === selected()"
              (triggered)="choose(voice.id)">{{ voice.name }}</button>
    }
  </div>
</nostos-menu>
```

## PlaybackSpeedMenuComponent

**Selector:** `nostos-playback-speed-menu`  
**Files:** `src/app/ui/playback-speed-menu/`

The one playback-speed control (read aloud and the audiobook player). An
`appButton` capsule shows the rate and opens a `nostos-menu`: the common rates
as `nostosMenuItemRadio` choices with the current one checked and, when `step`
is set, a "− rate +" row of menu items that keep the menu open between presses.

| Input | Type | Default | Description |
| --- | --- | --- | --- |
| `rate` | `number` | required | The rate playing now |
| `rates` | `readonly number[]` | required | The common rates listed as choices |
| `step` | `number \| null` | `null` | Fine step; `null` offers the presets only |
| `min` / `max` | `number \| null` | slowest / fastest preset | The player's supported range; the step stays inside it |
| `label` | `string` | `'Playback speed'` | Names the trigger and the menu |
| `variant` | `ButtonVariant` | `'ghost'` | The trigger's appButton variant |

Output: `rateChange(number)`. The trigger's height is the caller's, through
`--playback-speed-trigger-height` on the host (default `--control-h-xs`); a
player bar used without looking sets `var(--control-h-touch)`.

```html
<nostos-playback-speed-menu label="Playback speed" variant="secondary"
  [rate]="rate()" [rates]="[0.8, 1, 1.25, 1.5, 2]" [step]="0.05" [min]="0.5" [max]="3"
  (rateChange)="setRate($event)" />
```

## SettingsPopoverComponent

**Selector:** `nostos-settings-popover`  
**Files:** `src/app/ui/settings-popover/`

The panel a toolbar button opens: the reader's View settings and the Studio's
Page settings. It is one surface on the CDK overlay with one set of rules, so a
new settings panel does not hand-roll its own positioning, scrim or Escape:

- it hangs under its `origin` button (end-aligned, flipped when there is no
  room) and follows it on scroll and resize;
- its width is the `--settings-popover-width` token (19rem); below 640px
  (`SETTINGS_POPOVER_SHEET_QUERY`) it is a full-width sheet on the bottom edge
  with a grab handle;
- Escape and a press outside emit `dismissed` (`'escape' | 'outside'`), and the
  caller closes it. A press on `origin` is the button's own toggle. A layer
  inside that handles Escape itself (a sub-view's "back") calls
  `preventDefault()` and the panel leaves it alone;
- focus moves in on opening (`initialFocus`: `true` for the first control, a
  selector, or `false` when the caller places it) and back to `origin` when it
  closes with focus inside it (`restoreFocus`). After a press elsewhere, focus
  stays where the press went.

It is non-modal (`role="dialog"`, `aria-modal="false"`) and does not dim the
page: a setting is judged on the page itself. It exists while it is open, so
wrap it in `@if`. The caller lays out the rows; the panel owns the surface and
padding.

```html
@if (open()) {
  <nostos-settings-popover [origin]="aaButton" label="View settings"
                           (dismissed)="open.set(false)">
    …rows…
  </nostos-settings-popover>
}
```

## RangeDirective

**Selector:** `input[type=range][appRange]`  
**Files:** `src/app/ui/range/`

The one slider: a 4px track filled in the accent up to the value, a 14px
thumb. The native input stays the control (value, min, max, step, keys,
labelling). Scrubbers use it too (the audiobook and the passage clip); the
directive repaints when `min`/`max` change under the same value, so a
duration that arrives late still fills the track correctly.

## UI catalogue / visual fixture

**Route:** `/ui-catalogue`  
**Component:** `src/app/ui/ui-catalogue/`

The catalogue renders the canonical Button/IconButton variants and sizes, native form
controls, FormField help/error states, Switch, Chip, Badge/Status, segmented semantics,
and ModalShell/DialogActions composition. It intentionally uses the production theme
service and token graph. Playwright verifies light/dark, desktop/mobile, disabled,
busy, destructive, validation, checked/selected, hover and focus states.

Use the catalogue when adding or changing a generic primitive. If a proposed control
does not fit the catalogue without hiding a product-specific semantic contract, it
probably belongs to the product layer instead.

---

## Page chrome

Shared by every page that has one (issue #1207). Use these; do not restyle them.

| Piece | How | Notes |
| --- | --- | --- |
| Page title | `<h1 class="page-title">` | `--page-title-size` (1.75rem), medium weight, 2.4rem line. Library, Brain, To read, Settings. A page may shrink it on a phone. |
| Back link | `<a appButton variant="ghost" size="sm" class="back-link" routerLink="…">` with `<nostos-icon name="arrow-left" [size]="14" />` and the label in a `<span>` | `.back-link` pulls it left by its padding so the arrow lines up with the title. An in-panel back that does not navigate (Studio's reference panel) is the same thing on a `<button>`. |
| Brand lockup | `<nostos-brand-lockup />` | `src/app/ui/brand-lockup/`. The tile + wordmark, 40px on the mark. `--brand-lockup-word-opacity` and `--brand-lockup-word-transition` let a host fade the word (the Library's collapsed rail). |
| Side navigation | `.side-nav` on the root, then `.sidebar-header`, `.nav-scroll-area`, `.nav-group`, `.section-head`, `.section-label`, `.nav-item` (`.active` or `.is-active`), `.count-badge` | `styles.css`. The Library sidebar, the Brain sidebar and the Settings nav. The tint is a `::before`, so a sidebar can morph it without moving the icon. |
| Toolbar edge | `.toolbar-edge` on a sticky glass toolbar | No hairline at rest, one once content scrolls under it (scroll-driven; older browsers keep the hairline). |
| Drawer scrim | `.drawer-scrim` behind a phone drawer | A plain `--overlay-medium` dim, no blur. Show it only at drawer widths. |

---

## InlineRenameComponent

**Selector:** `nostos-inline-rename`
**Files:** `src/app/ui/inline-rename/`

The one way to rename a thing where its name is shown: the flat tree's rows (Library
collections, Studio documents), a Brain topic in the index and at the top of its page.

```html
<nostos-inline-rename
  [name]="topic.name"
  [error]="renameError()"
  [busy]="renaming()"
  (committed)="save($event)"
  (cancelled)="stopRenaming()"
/>
```

- Opens focused with the old name selected.
- **Enter** saves, **Escape** cancels, **leaving the field** saves if the name changed
  and is not empty; an unchanged or emptied field just closes.
- A name that cannot be used is said **under the field** and the field stays open:
  empty on Enter (`requiredMessage`), refused by `[validate]` (a duplicate), or refused
  by the server (`[error]`, hidden again once the user types).
- No Save or Cancel buttons. `committed` carries the trimmed new name and only fires
  when it differs from `name`; the owner decides what saving does.
- `variant="title"` edits a page heading: the heading's face, a rule instead of a box.

---

## "Pick a book" rows

Wherever the user chooses a library book from a list (the Editions dialog's "Link another
book", highlight import's "Choose a book", Studio's book index), each book is one
`appChoiceRow` (`src/app/ui/choice-row/`), with the same three tiers:

```html
<button type="button" appChoiceRow (click)="pick(book)">
  <span class="nostos-choice-row__text">
    <span class="nostos-choice-row__title">{{ book.title }}</span>
    <span class="nostos-choice-row__sub">{{ book.author || 'Unknown author' }}</span>
    <span class="nostos-choice-row__meta">
      <nostos-icon [concept]="format" [size]="12" aria-hidden="true" /> {{ formatLabel }}
    </span>
  </span>
</button>
```

`bookFormatConcept(type, fileName)` and `BOOK_FORMAT_LABEL` (`src/app/core/book-format.ts`)
give the format concept and its short name. A book with no author reads "Unknown author"
everywhere (`UNKNOWN_AUTHOR`).

---

## FlatTreeComponent

**Selector:** `app-flat-tree`  
**Files:** `src/app/ui/flat-tree/`

A generic hierarchical tree rendered as a flat list with indent levels. Used by the library sidebar (collections) and writing studio (file tree).

### Inputs

| Input       | Type                  | Description                                                          |
| ----------- | --------------------- | -------------------------------------------------------------------- |
| `items`     | `any[]` (required)    | Flat array with `id`, `name`, `parentId` fields                      |
| `activeId`  | `string \| undefined` | Currently selected node ID                                           |
| `typeField` | `string`              | Property name to distinguish folder vs. document (default: `'type'`) |
| `editingId` | `string \| undefined` | Node currently being renamed (shows `nostos-inline-rename`)          |
| `renameError` | `string \| null`     | The owner's refusal of the last rename, said under the field         |
| `renameBusy` | `boolean`             | A rename is being saved                                              |
| `rejectDuplicateNames` | `boolean`   | Refuse a sibling's name before sending (Library collections)         |

### Outputs

| Output                | Type                              | Description                      |
| --------------------- | --------------------------------- | -------------------------------- |
| `nodeSelected`        | `EventEmitter<TreeNode>`          | Node clicked                     |
| `nodeMoved`           | `EventEmitter<TreeNodeMoveEvent>` | Drag-dropped onto new parent     |
| `nodeRenamed`         | `EventEmitter<{id, name}>`        | Inline rename triggered          |
| `nodeDeleted`         | `EventEmitter<string>`            | Delete requested                 |
| `nodeRenameSaved`     | `EventEmitter<{id, newName}>`     | Rename sent (Enter, or blur with a changed name) |
| `nodeRenameCancelled` | `EventEmitter<void>`              | Rename closed (Escape, or blur with nothing changed) |

### Helper: `buildFlatTree()`

**File:** `src/app/ui/flat-tree/flat-tree.helper.ts`

Converts a flat `parentId`-based array into an ordered `FlatTreeNode[]`:

1. Groups items by `parentId` (normalizes `undefined` → `null`)
2. Sorts: folders first, then alphabetical by `name`
3. Recursively walks the tree, only emitting children of expanded folders
4. Sets `expandable = true` only for folders that actually have children
5. Returns flat array with `level` (indent depth) for CSS indentation

### Drag & Drop

Uses `@angular/cdk` `DragDropModule`. Drop targets:

- **Folder node:** Reparents the dragged item under that folder
- **Root area:** Moves item to root (`newParentId = null`)

Drag is disabled during inline rename (`editingId` matches node).

---

## NoteItemComponent

**Selector:** `app-note-item`  
**File:** `src/app/ui/note-item/`

One note, the same everywhere a note is shown: the Brain's lists and note page,
a book's notes, the reader's notes panel and the Studio's inspected source. No
box: the quote in the pen's accent rail, the reader's own words, the topics as
chips (open, remove, add with a picker), where the note comes from, and a `⋯`
`nostos-menu` for the rest. A passage shows its title, size and opening and is
read in the passage viewer. The item owns no data: it says what was asked for
through outputs and shows whatever note it is given back. It sizes itself from
its own width (a container query), so a narrow panel on a wide screen gets the
narrow layout.

### Inputs

| Input                  | Type                                  | Default | Description |
| ---------------------- | ------------------------------------- | ------- | ----------- |
| `note`                 | `NoteSearchHit` (required)            | —       | The note; `noteAsItem(note, topicNames)` turns a book's `Note` into one |
| `topics` / `topicMap`  | `TopicDto[]` / `Map<string, TopicDto>` | `[]` / built | Every topic, for the picker, chips and inline `[[links]]` |
| `citedWritings`        | `CitingWriting[]`                     | `[]`    | "Cited in N writings" in the meta line |
| `showBook`             | `boolean`                             | `true`  | False where a heading already names the book |
| `showMoreFromBook`     | `boolean`                             | `true`  | Offer "More from this book" |
| `canSelect` / `selectable` / `selected` | `boolean`            | `true` / `false` / `false` | Multi-select in lists |
| `busy`                 | `boolean`                             | `false` | A topic change is in flight |
| `suggestions` / `suggestionState` | `string[]` / state         | `[]` / `'off'` | AI topic suggestions for the picker |
| `inBook`               | `boolean`                             | `false` | Shown inside its own open book: "Show in book" emits `jumpRequested` instead of routing |
| `audioTime`            | `number \| null`                      | `null`  | An audiobook note's time; leads the meta line and emits `jumpRequested` |
| `passageViewerOptions` | `Omit<PassageViewerRequest, 'noteId'>` | `null` | Passed to the passage viewer (the reader's own "Open in book") |
| `readOnly`             | `boolean`                             | `false` | Display only: no menu, chips or capture details; `[[links]]` stay in the words |
| `showMeta`             | `boolean`                             | `true`  | False where the surface names the source itself |

### Outputs

`topicAdded`, `topicRemoved`, `pickerOpened`, `pickerClosed`, `edited`
(`NoteItemEdit`: only what changed), `keepWithWriting`, `moreFromBook`,
`selectRequested`, `selectedChange`, `deleteRequested`, `restored`, and
`jumpRequested` (with `inBook` or `audioTime`).

### Features

- **Editing:** "Edit note" in the menu opens the quote (or passage title), the four pens and the words, with Save / Cancel
- **Long notes:** a quote over 420 characters or words over 600 are cut to five lines behind "Show all"
- **Topic tags:** chips under the words; an inline `[[Topic]]` opens its topic in the app

---

## QuoteBlockComponent

**Selector:** `[appQuote]`
**File:** `src/app/ui/quote-block/`

A quotation from a book, drawn one way everywhere (issue #1203): the note's
quote, Brain Sort, the Studio's source rows, the `@` source palette and the
reader's selection and highlight card. Serif words beside a square 2px rail in
the pen's colour (`highlightFillRef()`), and **no added quotation marks**: the
rail says it is a quote. With no known pen the rail stays the neutral hairline.
It goes on the element that holds the words, so a `<span>` inside a row's
button (where a `<blockquote>` may not go) reads the same as a blockquote.

```html
<blockquote appQuote [pen]="quotePen(note)">{{ note.selectedText }}</blockquote>
<span appQuote size="sm" [lines]="2" [pen]="pen">{{ preview }}</span>
```

| Input   | Type                            | Default | Description |
| ------- | ------------------------------- | ------- | ----------- |
| `pen`   | `HighlightColour \| null`       | `null`  | The pen; `quotePen(note)` gives null when the payload does not carry it |
| `size`  | `'sm' \| 'md' \| 'lg'`          | `'md'`  | A row or panel, a note, a page of its own (Brain Sort) |
| `lines` | `number \| null`                | `null`  | Clamp to this many lines |

`--nostos-quote-ink` recolours the words (the Studio's manuscript ink); a
consumer may set `--nostos-accent-rail-color` for its own neutral rail.

---

## PenSwatchesComponent

**Selector:** `app-pen-swatches`
**File:** `src/app/ui/pen-swatches/`

The four highlighter pens as a labelled group of 22px dots with one active
ring (`aria-pressed`), used by the reader's notes panel, selection bar and
highlight card and by a note being edited. On touch each dot answers a 34x44px
tap through an invisible `::before`, so neighbours on the 34px pitch never
overlap.

| Input / output  | Type                       | Description |
| --------------- | -------------------------- | ----------- |
| `value`         | `HighlightColour \| null`  | The chosen pen |
| `disabled`      | `boolean`                  | While a save is in flight |
| `label`         | `string`                   | The group's name (default "Highlight colour") |
| `testIdPrefix`  | `string \| null`           | `data-testid="<prefix><pen>"` on each dot |
| `(chosen)`      | `HighlightColour`          | A dot was picked |

---

## TopicChipComponent

**Selector:** `app-topic-chip`
**File:** `src/app/ui/topic-chip/`

A topic on something (issue #1204): a quiet pill with the topic mark and the
name, in the note's topics, Brain Sort's chosen topics, a cited source's topics
in the Studio and, with `[icon]="null"`, a book's collections in the collection
picker. The name opens the topic when given a `link`, or reports `activated`
when `actionable` (the Studio saves the writing first). `removable` adds an
`appIconButton` cross. On touch the name and the cross grow invisible 44px hit
areas.

| Input / output | Type                        | Default   | Description |
| -------------- | --------------------------- | --------- | ----------- |
| `name`         | `string` (required)         | —         | The topic |
| `link`         | router link \| `null`       | `null`    | Opens the topic |
| `actionable`   | `boolean`                   | `false`   | Without a link: the name is a button emitting `activated` |
| `removable`    | `boolean`                   | `false`   | Show the cross, emitting `removed` |
| `disabled`     | `boolean`                   | `false`   | Disables the cross |
| `icon`         | `NostosConcept \| null`     | `'topic'` | The mark before the name |
| `removeLabel`  | `string \| null`            | `null`    | The cross's name when not "Remove topic <name>" |

---

## TopicOptionComponent

**Selector:** `[appTopicOption]`
**File:** `src/app/ui/topic-option/`

One row in any list of topics: the topic mark, the name and the usage count
as a plain muted number (never a badge). `kind="create"` reads Create “name”
after a plus; `kind="suggestion"` is muted. It goes on the element the list
needs (`<li role="option">` or `<button>`), which keeps its role, id,
`aria-selected` and events. Used by `app-topic-picker-popover`, the `[[`
suggestions and Link a topic dialog, Brain Sort and the merge picker.

The file also holds the one wording for an empty topic list:
`NO_TOPICS_YET` ("No topics yet. Type a name to create one.") and
`NO_TOPICS_MATCH` ("No topics match.").

---

## StarRatingComponent

**Selector:** `app-star-rating`  
**File:** `src/app/ui/star-rating/`

Click-to-rate stars (0–5). Clicking the same star resets to 0.

| Input      | Type      | Description             |
| ---------- | --------- | ----------------------- |
| `rating`   | `number`  | Current rating value    |
| `readonly` | `boolean` | Disable interaction     |
| `size`     | `string`  | CSS size for star icons |

| Output         | Payload  |
| -------------- | -------- |
| `ratingChange` | `number` |

---

## TopicInputComponent

**Selector:** `app-topic-input`  
**File:** `src/app/ui/topic-input.component/`

Textarea with wiki-link autocomplete. Implements `ControlValueAccessor` for form integration.

- Provides its own `TopicAutocompleteService` at the component level
- Typing `[[` triggers the autocomplete panel
- Selecting a topic replaces `[[partial` with `[[Topic Name]] `
- Arrow keys navigate suggestions; Enter selects; Escape dismisses

---

## TopicAutocompletePanel

**Selector:** `topic-autocomplete-panel`  
**File:** `src/app/ui/topic-autocomplete-panel/`

Dropdown overlay rendered by `TopicInputComponent`. Reads `suggestions()` and `activeIndex()` from `TopicAutocompleteService`.

---

## MarkdownEditorComponent

**Selector:** `app-markdown-editor`  
**File:** `src/app/ui/markdown-editor/`

The Studio's page: a TipTap editor that loads and saves Markdown.

| Input            | Type                  | Description              |
| ---------------- | --------------------- | ------------------------ |
| `initialContent` | `InputSignal<string>` | Markdown content to load |

| Output          | Payload             |
| --------------- | ------------------- |
| `contentChange` | `string` (markdown) |

### Implementation

- Markdown is converted to HTML with `marked` on load and back with Turndown + GFM tables on change (`markdown-conversion.ts`)
- Formatting comes from a selection bubble, the `/` menu (`block-menu.ts`) and Markdown shortcuts; there is no toolbar
- `@` reports a query to the host (`source-suggestion.extension.ts`); the Studio draws the palette and inserts the result
- Optional typewriter sounds (`typewriter-sounds.ts`, CC0 samples in `public/audio/typewriter/`)
- Page typography comes from `--manuscript-*` tokens, set by the Studio's page settings

---

## EmptyStateComponent

**Selector:** `nostos-empty-state`
**File:** `src/app/ui/empty-state/`

What a page or pane says when it has nothing to show, or when a load failed
(issue #1202). One look everywhere: centred in the space it is given, an
optional quiet glyph (`icon` or `concept`), one line in the reading ink, an
optional quieter `detail` line, and the projected actions under them. No box,
border or fill. `compact` is the same for a narrow pane (a sidebar list, the
Reader's notes panel, a Studio reference pane, a settings card): less air, a
24px glyph and the small type step.

`error` makes it the in-page failure state: `role="alert"` and a **Try again**
button that emits `retry` (`TRY_AGAIN` is the one retry label). Set
`[retryable]="false"` when nothing can be retried; the message then says what
to do. Projected actions (Back, Clear filters) sit before the retry.

```html
<nostos-empty-state concept="note" message="No notes yet." detail="Highlight text to add one." />
<nostos-empty-state error message="Your books could not be loaded." (retry)="retryLoad()" />
<nostos-empty-state compact message="No books match this search." />
```

| Input       | Type                     | Default | Description |
| ----------- | ------------------------ | ------- | ----------- |
| `icon`      | `NostosIconName \| null` | `null`  | A glyph; prefer `concept` for a Nostos thing |
| `concept`   | `NostosConcept \| null`  | `null`  | A semantic icon (`note`, `topic`, `unsorted`…) |
| `message`   | `string \| null`         | `null`  | The one line |
| `detail`    | `string \| null`         | `null`  | A quieter second line |
| `compact`   | `boolean`                | `false` | The narrow-pane variant |
| `error`     | `boolean`                | `false` | A failed load: alert + Try again |
| `retryable` | `boolean`                | `true`  | Whether `error` offers Try again |

Loading uses `nostos-loading-indicator` for page and pane loads, not text or
skeleton rows: the call site centres it where the content will be.

---

## PagingStatusComponent

**Selector:** `nostos-paging-status`
**File:** `src/app/ui/paging-status/`

The end of a paged list (Library, Brain): the next page loads as the reader
scrolls toward it (`InfiniteScrollDirective` on the list end), a small spinner
shows while it comes, and a failed page is replaced in place by its message and
an inline **Try again**. Nothing is asked again on its own after a failure.

A **Load more** button stays in the tab order for keyboards and screen readers:
out of sight until focused while scrolling does the work, and visible where the
browser has no `IntersectionObserver`.

```html
<nostos-paging-status
  [hasMore]="notes().length < total()"
  [loading]="loading()"
  [failed]="failed()"
  [root]="scroller"
  label="Loading more notes"
  failedMessage="More notes could not be loaded."
  (more)="loadMore()"
  (retry)="loadPage()"
/>
```

| Input           | Type              | Default                       | Description |
| --------------- | ----------------- | ----------------------------- | ----------- |
| `hasMore`       | `boolean`         | `false`                       | Whether a next page exists; nothing is drawn otherwise |
| `loading`       | `boolean`         | `false`                       | A page is on its way |
| `failed`        | `boolean`         | `false`                       | The last page failed |
| `label`         | `string`          | `'Loading more'`              | What the spinner announces |
| `failedMessage` | `string`          | `'More could not be loaded.'` | The failure line |
| `root`          | `Element \| null` | `null`                        | The scrolling element; null is the viewport |
| `rootMargin`    | `string`          | `'200px 0px'`                 | How far ahead to ask |

Outputs: `more` (the end came into view, or Load more was pressed) and `retry`.

---

## ToastContainerComponent

**Selector:** `app-toast-container`  
**File:** `src/app/ui/toast-container/`  
**Entry point:** `ToastService` in `src/app/core/services/toast.service.ts`

Toast is the canonical Nostos pattern for **transient, non-blocking feedback**. The root
container is mounted once by `App`; feature code publishes through `ToastService`
rather than rendering local snackbar/toast surfaces.

Use a toast when an action has completed or a short-lived state is useful to know
without interrupting the current task. Keep blocking failures, retry/decision states,
and information that remains relevant in the product surface itself. High-frequency
background diagnostics belong in logs unless the user has something actionable to do.

### Variants and announcements

| Variant | Visual role | Assistive-technology contract |
| --- | --- | --- |
| `success` | restrained success accent | `role="status"`, `aria-live="polite"` |
| `info` | neutral/primary accent | `role="status"`, `aria-live="polite"` |
| `error` | restrained danger accent | `role="alert"`, `aria-live="assertive"` |

The icon and message live inside the announced region. The dismiss action is a sibling
`button[appIconButton]`, not a descendant of that region, so “Dismiss notification”
is not read as part of the message. Toast appearance never moves focus; the native
dismiss button remains keyboard reachable and carries both `aria-label` and `title`.

### Lifetime, stack and copy

- success/info default to 4 seconds; errors default to 5 seconds;
- identical message + variant pairs are deduplicated and restart as one toast;
- the visible stack is capped at three;
- every toast can be dismissed explicitly;
- copy should be concise action/result language: “Book details saved” rather than
  “Book details were saved successfully.”;
- a one-sentence toast has no full stop; a toast with a second sentence (“Could not
  save the note. Try again.”) punctuates both;
- failures start “Could not …” (not “Failed to …”, “Unable to …” or “X could not be
  …”); a named item is quoted: `Deleted “Title”`;
- do not convert persistent Reader/open/retry failures into transient feedback merely
  for visual consistency.

### Motion, placement and layers

Toast enter/leave uses Angular's current `animate.enter` / `animate.leave` path with
`--motion-fast` and canonical easing tokens. Only opacity/transform animate; reduced
motion removes the spatial animation.

Desktop uses a compact top-right stack. Mobile switches to stable left/right gutters and
includes safe-area insets. The container uses `--layer-system`: notifications sit above
Assistant (`--layer-assistant`) and ordinary modal layers so system feedback is not
hidden, while their top-edge placement avoids the bottom-anchored Assistant/Reader
controls. The catalogue exposes success, info, error, long-copy and stacked states at
desktop/mobile widths in light and dark themes.

---

## Directives

### InfiniteScrollDirective

**Selector:** `[appInfiniteScroll]`  
**File:** `src/app/core/directives/infinite-scroll.directive.ts`

The paging sentinel shared by Library and Brain (through `nostos-paging-status`).
Emits `(reached)` when the host comes within `rootMargin` of `root` (the
viewport by default). While `paused` is true (a page is loading, or the last
one failed) nothing is emitted; when it turns false the host is observed
afresh, so a list end still in view after a short page asks for the next one.

```html
<div appInfiniteScroll [root]="scroller" rootMargin="600px 0px" [paused]="loading()" (reached)="loadMore()"></div>
```

### TopicAutocompleteDirective

**Selector:** `[noteAutocomplete]`  
**File:** `src/app/core/directives/topic-autocomplete.directive.ts`

Applied to a `<textarea>`. Listens for `input` events and pipes text + cursor position into `TopicAutocompleteService.update()`. Handles keyboard navigation (arrows, Enter) and emits `(insertTopic)`.

---

## Pipes

### NostosDatePipe

**Name:** `nostosDate`  
**File:** `src/app/ui/pipes/nostos-date.pipe.ts` (formats in `src/app/core/date-formats.ts`)

Every date a reader sees has one of two shapes: a date, `d MMM y` ("24 Oct 2026"), or a
date and time, `d MMM y, HH:mm` ("24 Oct 2026, 14:05"), in the browser's time zone and
without naming it. Use the pipe in templates instead of Angular's `date` pipe, and
`formatNostosDate()` in TypeScript instead of `toLocaleString()` or `Intl.DateTimeFormat`.

```html
{{ note.createdAt | nostosDate }}
{{ backup.createdAtUtc | nostosDate: 'datetime' }}
```

A missing or unreadable value renders as an empty string.

### NoteFormatPipe

**Name:** `noteFormat`  
**File:** `src/app/ui/pipes/note-format.pipe.ts`

Transforms note content by replacing `[[Topic Name]]` patterns with clickable HTML spans:

```html
{{ note.content | noteFormat: topicMap }}
```

Lookup is case-insensitive. Unresolved topics render as plain `[[text]]`.
