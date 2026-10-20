/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { NOSTOS_CONCEPTS, type NostosConcept } from './nostos-concepts';
import { NOSTOS_ICONS, type NostosIconName, type NostosIconWeight } from './nostos-icons';

/**
 * The general-purpose Nostos icon — for every icon that is NOT a button.
 *
 * Button glyphs go through `button[appIconButton]` (which renders this component
 * internally), because a button owns sizing, tone, hover and pressed semantics.
 * Everything else — nav rows, badges, empty states, inline affordances — is this.
 *
 * USAGE
 * -----
 *   <nostos-icon name="trash" />                       // a glyph
 *   <nostos-icon concept="audiobook" [size]="14" />    // a stable product concept
 *   <nostos-icon name="star" weight="fill" [size]="18" />
 *   <nostos-icon name="warning" [label]="'Sync failed'" />
 *
 * TWO WAYS IN, ON PURPOSE — `name` for a glyph, `concept` for a product idea.
 * A concept resolves through `NOSTOS_CONCEPTS`, so the ONE place that decides
 * "audiobook is headphones" is a mapping table, not 20 templates. One of the two
 * is required; `name` wins if both are given. Passing neither is a programming
 * error and throws loudly rather than rendering an invisible box.
 *
 * ACCESSIBILITY: DECORATIVE BY DEFAULT, OPT IN TO MEANING.
 * An icon with no `label` renders `aria-hidden="true"`. That is the correct and
 * overwhelmingly common case — the glyph repeats what the adjacent text or the
 * parent button already says, and a second announcement is noise. Setting
 * `[label]` (a string) switches the host to `role="img"` + `aria-label` for the
 * rare icon that carries meaning on its own. There is deliberately no third
 * option and no `decorative` flag to contradict the label.
 *
 * COLOR comes from `currentColor` (Phosphor draws with `fill="currentColor"`), so
 * an icon inherits its surface's semantic token for free and no call site sets an
 * icon colour. Size is the only inline style, and it is written as `width`/
 * `height` ATTRIBUTES on the svg so ordinary CSS can still override it.
 *
 * WHY `[innerHTML]` + `bypassSecurityTrustHtml`
 * ---------------------------------------------
 * The markup is a build-time constant imported from `@phosphor-icons/core` (see
 * the loader note in `nostos-icons.ts`); no user data ever reaches it, so the
 * sanitizer has nothing to protect against and would only strip the SVG. The
 * bypass is applied to that constant and nowhere else.
 */
@Component({
  selector: 'nostos-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="nostos-icon__glyph" [innerHTML]="markup()"></span>`,
  host: {
    '[attr.aria-hidden]': 'ariaHidden()',
    '[attr.role]': 'roleAttr()',
    '[attr.aria-label]': 'labelAttr()',
  },
  styles: [
    `
      /* Display inline-flex collapses the host's line box so the host box
         hugs the glyph dimensions, eliminating font descender slack that
         previously caused glyphs to render above centre in centred containers.
         The wrapper span is display:contents: it exists only to be the
         [innerHTML] target, so the svg is laid out as if it were a direct
         child of the host. */
      :host {
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
      .nostos-icon__glyph {
        display: contents;
      }
      .nostos-icon__glyph > svg {
        vertical-align: baseline;
      }
    `,
  ],
})
export class NostosIconComponent {
  /** A Phosphor glyph name from the registry. Checked at compile time. */
  readonly name = input<NostosIconName | null>(null);

  /** A stable Nostos concept; resolved through `NOSTOS_CONCEPTS`. */
  readonly concept = input<NostosConcept | null>(null);

  /**
   * Phosphor weight. `regular` is the system default; use `fill` for a
   * selected/active state and the others only where the design calls for it.
   * A weight that was not imported for that glyph degrades to `regular`.
   */
  readonly weight = input<NostosIconWeight>('regular');

  /** Glyph size in px. The shared rungs live in `NOSTOS_ICON_SIZE`. */
  readonly size = input<number>(16);

  /** Accessible name. Absent by default: the icon is decorative. */
  readonly label = input<string | null>(null);

  private readonly sanitizer = inject(DomSanitizer);

  /** The glyph finally rendered: explicit `name` wins over `concept`. */
  readonly resolvedName = computed<NostosIconName>(() => {
    const explicit = this.name();
    if (explicit) return explicit;
    const concept = this.concept();
    if (concept) return NOSTOS_CONCEPTS[concept];
    throw new Error(
      'nostos-icon: no icon requested. Pass [name]="<glyph>" or [concept]="<nostos concept>".',
    );
  });

  /** Raw svg markup for the resolved glyph at the requested weight. */
  private readonly svg = computed(() => {
    const variants = NOSTOS_ICONS[this.resolvedName()];
    return variants[this.weight() as keyof typeof variants] ?? variants.regular;
  });

  /** The svg with its size written in, ready to be inserted. */
  readonly markup = computed<SafeHtml>(() => {
    const sized = this.svg().replace('<svg', `<svg width="${this.size()}" height="${this.size()}"`);
    return this.sanitizer.bypassSecurityTrustHtml(sized);
  });

  protected readonly ariaHidden = computed(() => (this.label() ? null : 'true'));
  protected readonly roleAttr = computed(() => (this.label() ? 'img' : null));
  protected readonly labelAttr = computed(() => this.label() ?? null);
}
