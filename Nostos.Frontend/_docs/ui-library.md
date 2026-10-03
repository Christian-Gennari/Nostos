# Frontend — UI Components Library

Reusable components and utilities shared across pages.

## Start here: Nostos UI v1

This is the **implementation reference** for shared UI. The measured visual language
and the reasons behind it live in [`docs/design/design-language.md`](../docs/design/design-language.md);
the CSS/token graph remains the visual source of truth. The live fixture is available
at **`/ui-catalogue`** and is covered by `e2e/ui-catalogue.spec.ts` at desktop/mobile
widths in both light and dark themes.

Nostos UI v1 has four layers:

| Layer | Owner | Examples |
| --- | --- | --- |
| **Foundations** | `src/styles.css` token graph | colour, type, radius, elevation, motion, focus, control heights |
| **Primitives** | `src/app/ui/` | Button, IconButton, Input/Textarea, Dropdown, Switch, Chip, Badge |
| **Patterns** | shared composition/recipes | FormField, ModalShell + DialogActions, Toast, segmented visual recipe |
| **Product components** | feature surfaces | BookCard, NoteCard, Reader transport, Studio editor/tree, assistant recording, acquisition workflows |

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
Library, NoteCard and Studio use different measured radii and selected-state contracts;
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

## UI catalogue / visual fixture

**Route:** `/ui-catalogue`  
**Component:** `src/app/ui/ui-catalogue/`  
**Playwright:** `e2e/ui-catalogue.spec.ts`

The catalogue renders the canonical Button/IconButton variants and sizes, native form
controls, FormField help/error states, Switch, Chip, Badge/Status, segmented semantics,
and ModalShell/DialogActions composition. It intentionally uses the production theme
service and token graph. Playwright verifies light/dark, desktop/mobile, disabled,
busy, destructive, validation, checked/selected, hover and focus states.

Use the catalogue when adding or changing a generic primitive. If a proposed control
does not fit the catalogue without hiding a product-specific semantic contract, it
probably belongs to the product layer instead.

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
| `editingId` | `string \| undefined` | Node currently being renamed (shows inline input)                    |

### Outputs

| Output                | Type                              | Description                      |
| --------------------- | --------------------------------- | -------------------------------- |
| `nodeSelected`        | `EventEmitter<TreeNode>`          | Node clicked                     |
| `nodeMoved`           | `EventEmitter<TreeNodeMoveEvent>` | Drag-dropped onto new parent     |
| `nodeRenamed`         | `EventEmitter<{id, name}>`        | Inline rename triggered          |
| `nodeDeleted`         | `EventEmitter<string>`            | Delete requested                 |
| `nodeRenameSaved`     | `EventEmitter<{id, name}>`        | Rename confirmed (Enter)         |
| `nodeRenameCancelled` | `EventEmitter<void>`              | Rename cancelled (Escape / blur) |

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

## NoteCardComponent

**Selector:** `app-note-card`  
**File:** `src/app/ui/note-card.component/`

Displays a single note with rich formatting. Supports inline editing, topic tags, and various display configurations.

### Inputs

| Input            | Type                      | Default | Description                      |
| ---------------- | ------------------------- | ------- | -------------------------------- |
| `note`           | `Note` (required)         | —       | Note data                        |
| `topicMap`     | `Map<string, TopicDto>` | `null`  | For rendering `[[Topic]]` tags |
| `showNavigation` | `boolean`                 | `true`  | Show "Go to book" link           |
| `showActions`    | `boolean`                 | `true`  | Show edit/delete buttons         |
| `showSource`     | `boolean`                 | `false` | Show book title source label     |
| `showDate`       | `boolean`                 | `true`  | Show creation date               |

### Outputs

| Output         | Payload                          | Description                   |
| -------------- | -------------------------------- | ----------------------------- |
| `update`       | `{ id, content, selectedText? }` | Note edited and saved         |
| `delete`       | `string` (note ID)               | Delete requested              |
| `topicClick` | `TopicDto`                     | `[[Topic]]` tag clicked     |
| `quoteClick`   | `Note`                           | "Insert quote" button clicked |
| `cardClick`    | `Note`                           | Card body clicked             |

### Features

- **Inline editing:** `startEdit()` → textarea → `saveEdit()` (Enter) / `cancelEdit()` (Escape)
- **Collapsible:** Notes > 250 chars show "Show more" / "Show less" toggle
- **Topic tags:** Content rendered via `NoteFormatPipe` — `[[Topic]]` becomes clickable colored spans
- **Selected text:** Displayed as a quote block above note content

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

Dropdown overlay rendered by `TopicInputComponent` and `NoteCardComponent`. Reads `suggestions()` and `activeIndex()` from `TopicAutocompleteService`.

---

## MarkdownEditorComponent

**Selector:** `app-markdown-editor`  
**File:** `src/app/ui/markdown-editor/`

WYSIWYG editor wrapping TinyMCE with markdown round-trip.

| Input            | Type                  | Description              |
| ---------------- | --------------------- | ------------------------ |
| `initialContent` | `InputSignal<string>` | Markdown content to load |

| Output          | Payload             |
| --------------- | ------------------- |
| `contentChange` | `string` (markdown) |

### Implementation

- Content is converted from markdown to HTML (`marked.parse()`) on load
- On every keystroke, HTML is converted back to markdown (`TurndownService`) and emitted
- Typography: Lora (serif) for body, Inter (sans-serif) for headings

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
- copy should be concise action/result language: “Book details saved.” rather than
  “Book details were saved successfully.”;
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

Attaches an `IntersectionObserver` to the host element (100px rootMargin). Emits `(scrolly)` when the element enters the viewport.

```html
<div appInfiniteScroll (scrolly)="loadMore()"></div>
```

### TopicAutocompleteDirective

**Selector:** `[noteAutocomplete]`  
**File:** `src/app/core/directives/topic-autocomplete.directive.ts`

Applied to a `<textarea>`. Listens for `input` events and pipes text + cursor position into `TopicAutocompleteService.update()`. Handles keyboard navigation (arrows, Enter) and emits `(insertTopic)`.

---

## Pipes

### NoteFormatPipe

**Name:** `noteFormat`  
**File:** `src/app/ui/pipes/note-format.pipe.ts`

Transforms note content by replacing `[[Topic Name]]` patterns with clickable HTML spans:

```html
{{ note.content | noteFormat: topicMap }}
```

Lookup is case-insensitive. Unresolved topics render as plain `[[text]]`.
