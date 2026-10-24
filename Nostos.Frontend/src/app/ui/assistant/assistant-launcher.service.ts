import { Injectable, computed, inject, signal } from '@angular/core';
import { modifierKeyLabel } from '../../core/platform';
import { Subject } from 'rxjs';

import { AssistantService } from './assistant.service';
import { AssistantStatusService } from './assistant-status.service';

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
  private readonly status = inject(AssistantStatusService);

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
    if (this.toggleRequested.observed) {
      this.toggleRequested.next();
      return;
    }
    // The shell has not loaded yet (it is deferred): keep the press and ask
    // for the shell now; it takes the press when it attaches.
    this.pendingToggle = !this.pendingToggle;
    this.assistant.surfaceWanted.set(true);
  }

  private pendingToggle = false;
  private started = false;

  /** For the shell when it attaches: whether a launcher was pressed before it existed. */
  takePendingToggle(): boolean {
    const pending = this.pendingToggle;
    this.pendingToggle = false;
    return pending;
  }

  /**
   * Cmd/Ctrl+J works before the assistant shell has loaded. Once the shell is
   * on screen it owns the shortcut itself, and this listener stands aside.
   */
  start(): void {
    if (this.started || typeof document === 'undefined') return;
    this.started = true;
    // The launchers (the dock's, the shell's capsule) show only once the
    // server says it can run the assistant; ask at boot, not when the
    // deferred shell happens to load.
    this.status.ensureLoaded();
    document.addEventListener('keydown', (event) => {
      if (this.toggleRequested.observed) return;
      const mod = event.metaKey || event.ctrlKey;
      if (!mod || event.altKey || event.key.toLowerCase() !== 'j' || !this.available()) return;
      event.preventDefault();
      this.toggle();
    });
  }
}

function resolveShortcutLabel(): string {
  return `${modifierKeyLabel()} J`;
}
