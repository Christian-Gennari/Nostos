import { Injectable, computed, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';

import { AssistantService } from './assistant.service';

/** Marks an element that opens Ask Nostos, so its press is not an outside click. */
export const ASSISTANT_LAUNCHER_ATTR = 'data-assistant-launcher';

/**
 * Who may open Ask Nostos, and where its launcher lives.
 *
 * The assistant component owns the conversation surface and its lifecycle
 * (focus, viewport tracking, voice). The launcher can live elsewhere: in the
 * workspace the dock carries it, and only routes without a dock (the reader)
 * fall back to the component's own floating capsule. This service is the seam
 * between the two, so neither has to know about the other's DOM.
 */
@Injectable({ providedIn: 'root' })
export class AssistantLauncherService {
  private readonly assistant = inject(AssistantService);

  /**
   * Whether Ask Nostos exists at all: the user wants it (preference on) AND the
   * server can run it (available). The dock segment, the floating capsule and
   * the panel all share this one gate.
   */
  readonly available = this.assistant.surfaceAvailable;

  /** Whether the panel is open, for the launcher's pressed state. */
  readonly isOpen = this.assistant.isOpen;

  /** A quiet, platform-appropriate shortcut hint for the launcher. */
  readonly shortcutLabel = resolveShortcutLabel();

  private readonly dockHosts = signal(0);

  /** True while a dock carries the launcher, so the floating capsule stands down. */
  readonly dockHosted = computed(() => this.dockHosts() > 0);

  /** Toggle requests from a launcher the component does not render itself. */
  readonly toggleRequested = new Subject<void>();

  /** Registers a dock-hosted launcher; call the returned function on destroy. */
  attachDock(): () => void {
    this.dockHosts.update((count) => count + 1);
    let attached = true;
    return () => {
      if (!attached) return;
      attached = false;
      this.dockHosts.update((count) => Math.max(0, count - 1));
    };
  }

  toggle(): void {
    if (!this.available()) return;
    this.toggleRequested.next();
  }
}

function resolveShortcutLabel(): string {
  if (typeof navigator === 'undefined') return 'Ctrl J';
  return /Macintosh|Mac OS X|iPhone|iPad|iPod/i.test(navigator.userAgent) ? '⌘ J' : 'Ctrl J';
}
