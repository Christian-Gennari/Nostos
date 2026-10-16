import { Injectable, signal } from '@angular/core';

/**
 * Whether the command palette is open.
 *
 * Ctrl/Cmd+K is the desktop way in; phones have no keyboard chord, so visible
 * controls (the Library's search-everything button, the More sheet) open it
 * through this service instead of reaching into the palette component.
 */
@Injectable({ providedIn: 'root' })
export class CommandPaletteService {
  readonly isOpen = signal(false);

  open(): void {
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  toggle(): void {
    this.isOpen.update((open) => !open);
  }
}
