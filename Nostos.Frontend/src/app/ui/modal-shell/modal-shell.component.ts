import { Component, HostListener, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CdkTrapFocus } from '@angular/cdk/a11y';

/**
 * How the shell presents itself.
 *
 * `sheet` is a form dialog: on a phone it takes the whole screen (see the
 * component's stylesheet for why), and on a wide viewport it is the centred
 * dialog it has always been. `dialog` is a compact question — a confirmation —
 * which stays centred at every width, because filling a phone screen with one
 * sentence and two buttons reads as an error state rather than a question.
 */
export type ModalShellVariant = 'sheet' | 'dialog';

/**
 * The geometry of the dialogs in Nostos, in one place.
 *
 * Before this existed the backdrop, the card, the internally scrolling body and
 * the pinned action row were copy-pasted into each dialog (`add-book-modal`,
 * `editions-modal`, `confirm-modal`), so "what a dialog looks like on a phone"
 * meant three edits and usually missed one. The shell owns that geometry.
 *
 * Callers own their content, including their own close control — every form
 * dialog here already has one, styled to its own header, and a second one
 * rendered by the shell would only be a duplicate to keep in sync.
 *
 * The regions, top to bottom:
 *
 * - `[shellHeader]` — pinned. The caller's header markup, padding and divider
 *   supplied by the shell.
 * - `[shellTabs]` — optional, pinned, never scrolls (a tab strip that scrolls
 *   away is a tab strip you cannot use).
 * - default projection — the scrolling body.
 * - `[shellActions]` — pinned action row.
 *
 * Only the body scrolls, so the close control and the primary action stay
 * reachable however long the content is.
 */
@Component({
  selector: 'app-modal-shell',
  standalone: true,
  imports: [CommonModule, CdkTrapFocus],
  templateUrl: './modal-shell.component.html',
  styleUrl: './modal-shell.component.css',
})
export class ModalShell {
  isOpen = input.required<boolean>();

  variant = input<ModalShellVariant>('sheet');

  /**
   * Desktop dialog width and height. Strings, because the callers' established
   * geometry differs (`90vh` for the book form, a computed maximum for the
   * editions list) and the shell must not quietly restyle them.
   */
  maxWidth = input<string>('700px');
  maxHeight = input<string>('90vh');

  /**
   * Stacking order. Kept per-caller because it encodes a real relationship: a
   * confirmation guarding a destructive action inside another dialog has to sit
   * above it (100 book form < 110 editions < 120 confirmation).
   */
  layer = input<number>(100);

  /** True while the caller is saving: locks every dismissal path. */
  busy = input<boolean>(false);

  /** Trap focus and return it to the opener when this dialog closes. */
  trapFocus = input<boolean>(false);

  /**
   * Whether a backdrop tap closes. Left at the callers' existing behaviour —
   * on a phone a `sheet` fills the screen, so there is no backdrop to mis-tap.
   */
  dismissOnBackdrop = input<boolean>(true);

  /** `alertdialog` for destructive questions, `dialog` for everything else. */
  dialogRole = input<'dialog' | 'alertdialog'>('dialog');

  /** ARIA wiring, forwarded verbatim so callers keep their labelling. */
  ariaLabel = input<string | null>(null);
  ariaLabelledBy = input<string | null>(null);
  ariaDescribedBy = input<string | null>(null);

  /** Extra class(es) on the card, for a caller's own content styling. */
  cardClass = input<string>('');

  /** A hairline under the header. Off for compact cards that pad their own. */
  headDivider = input<boolean>(true);

  closed = output<void>();

  /** Pointer id when the current gesture began directly on the backdrop. */
  private backdropPointerId: number | null = null;

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.isOpen() && !this.busy()) {
      this.closed.emit();
    }
  }

  onBackdropPointerDown(event: PointerEvent): void {
    // A drag that begins inside the card can finish over the backdrop. Remember
    // where the gesture began so that release is not mistaken for an outside click.
    this.backdropPointerId = event.target === event.currentTarget ? event.pointerId : null;
  }

  onBackdropPointerUp(event: PointerEvent): void {
    const beganOnBackdrop = this.backdropPointerId === event.pointerId;
    this.backdropPointerId = null;

    if (
      beganOnBackdrop &&
      event.target === event.currentTarget &&
      this.dismissOnBackdrop() &&
      !this.busy()
    ) {
      this.closed.emit();
    }
  }

  onBackdropPointerCancel(event: PointerEvent): void {
    if (this.backdropPointerId === event.pointerId) {
      this.backdropPointerId = null;
    }
  }
}
