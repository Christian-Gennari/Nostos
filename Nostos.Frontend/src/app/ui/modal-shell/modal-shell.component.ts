import {
  Component,
  DestroyRef,
  HostListener,
  effect,
  inject,
  input,
  output,
  ChangeDetectionStrategy,
} from '@angular/core';
import { ScrollLockDirective } from '../scroll-lock/scroll-lock.service';
import { CommonModule } from '@angular/common';
import { CdkTrapFocus } from '@angular/cdk/a11y';
import { ModalStack } from './modal-stack.service';
import { IconButtonComponent } from '../icon-button/icon-button.component';

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
 * Callers own their content. The close control is the shell's: a dialog names
 * it with `closeLabel` ("Close import dialog") and the shell draws the one
 * icon button, in the one place, disabled while `busy`. A dialog that is not
 * dismissable by a button (a confirmation, whose actions are its way out) omits
 * `closeLabel`.
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
  imports: [CommonModule, CdkTrapFocus, ScrollLockDirective, IconButtonComponent],
  templateUrl: './modal-shell.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './modal-shell.component.css',
})
export class ModalShell {
  isOpen = input.required<boolean>();

  variant = input<ModalShellVariant>('sheet');

  /**
   * Desktop dialog width and height. Strings, because the callers' established
   * geometry differs (`90dvh` for the book form, a computed maximum for the
   * editions list) and the shell must not quietly restyle them.
   */
  maxWidth = input<string>('700px');
  maxHeight = input<string>('90dvh');

  /**
   * Stacking order. Kept per-caller because it encodes a real relationship: a
   * confirmation guarding a destructive action inside another dialog has to sit
   * above it (100 book form < 110 editions < 120 confirmation).
   */
  layer = input<number>(100);

  /** True while the caller is saving: locks every dismissal path. */
  busy = input<boolean>(false);

  /**
   * Trap focus inside the dialog, move it in on open and give it back to the
   * opener on close. On by default: a dialog that leaves focus behind its own
   * backdrop is a keyboard dead end.
   */
  trapFocus = input<boolean>(true);

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

  /**
   * Draws the shell's close button with this accessible name. Leave unset for a
   * dialog that closes through its actions only.
   */
  closeLabel = input<string | null>(null);

  /** Whether the close button takes focus on open (a dialog with nothing to fill in). */
  closeFocusInitial = input<boolean>(false);

  closed = output<void>();

  /** Pointer id when the current gesture began directly on the backdrop. */
  private backdropPointerId: number | null = null;

  private readonly stack = inject(ModalStack);

  constructor() {
    // Open shells form a stack: only the top one answers Escape.
    let release: (() => void) | null = null;
    effect(() => {
      const open = this.isOpen();
      if (open && !release) release = this.stack.register(this);
      else if (!open && release) {
        release();
        release = null;
      }
    });
    inject(DestroyRef).onDestroy(() => release?.());
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: Event): void {
    if (!this.isOpen() || !this.stack.claimEscape(this, event)) return;
    // Busy locks every dismissal, but the key is still spent here.
    if (!this.busy()) this.closed.emit();
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
