import { DOCUMENT, DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * Whether the command palette is open, and the one shortcut that opens it.
 *
 * The palette component is deferred: it loads the first time it opens, so the
 * initial bundle does not carry its template and styles. That means the
 * shortcut cannot live on the component, which does not exist yet; it lives
 * here, started once by the app root. Phones have no keyboard chord, so the
 * More menu's Search everything item calls open() directly.
 */
@Injectable({ providedIn: 'root' })
export class CommandPaletteService {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private started = false;
  private dismissHandler: (() => void) | null = null;

  readonly isOpen = signal(false);

  /** Listens for Cmd/Ctrl+K for the life of the app; repeated calls are no-ops. */
  start(): void {
    if (this.started) return;
    this.started = true;
    const onKeydown = (event: KeyboardEvent) => {
      // Both plain K and Shift+K: plain Cmd/Ctrl+K collides with the browser's
      // own search in some setups, so either chord toggles the palette.
      const mod = event.metaKey || event.ctrlKey;
      if (!mod || event.altKey || event.key.toLowerCase() !== 'k') return;
      event.preventDefault();
      this.toggle();
    };
    this.document.addEventListener('keydown', onKeydown);
    this.destroyRef.onDestroy(() => this.document.removeEventListener('keydown', onKeydown));
  }

  /** The palette registers how to close with focus returned (see dismiss). */
  setDismissHandler(handler: (() => void) | null): void {
    this.dismissHandler = handler;
  }

  open(): void {
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  /** Closing from the shortcut is a dismissal: focus goes back where it was. */
  toggle(): void {
    if (!this.isOpen()) this.open();
    else if (this.dismissHandler) this.dismissHandler();
    else this.close();
  }
}
