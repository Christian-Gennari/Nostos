import { Component, inject, signal } from '@angular/core';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationStart,
  Router,
  RouterOutlet,
} from '@angular/router';
import { ToastContainerComponent } from './ui/toast-container/toast-container.component';
import { CommandPalette } from './ui/command-palette/command-palette.component';
import { HighlightImportModal } from './ui/highlight-import-modal/highlight-import-modal.component';
import { AssistantComponent } from './ui/assistant/assistant.component';
import { SwUpdateService } from './core/services/sw-update.service';
import { ThemeService } from './core/services/theme.service';
import { CloudEntryService } from './core/services/cloud-entry.service';
import { CloudEntryComponent } from './cloud-entry/cloud-entry.component';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    ToastContainerComponent,
    CommandPalette,
    HighlightImportModal,
    AssistantComponent,
    CloudEntryComponent,
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class App {
  private readonly router = inject(Router);
  readonly navigationPending = signal(false);
  readonly cloudEntry = inject(CloudEntryService);

  constructor() {
    // Applies the persisted theme immediately. `index.html` already set the
    // attribute before first paint; this reconciles the signal with the DOM and
    // owns the attribute for the rest of the session.
    inject(ThemeService).theme();

    // Keeps the service worker's navigation manifest fresh, so a deploy that
    // changes how URLs are served (the app shell vs. the API) reaches an
    // already-open client without a manual reload.
    inject(SwUpdateService).start();
    void this.cloudEntry.initialize();

    this.router.events.subscribe((event) => {
      if (event instanceof NavigationStart) this.navigationPending.set(true);
      if (
        event instanceof NavigationEnd ||
        event instanceof NavigationCancel ||
        event instanceof NavigationError
      ) {
        this.navigationPending.set(false);
      }
    });
  }
}
