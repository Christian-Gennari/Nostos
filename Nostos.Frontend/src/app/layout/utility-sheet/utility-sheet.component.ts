import {
  Component,
  HostListener,
  Injector,
  OnDestroy,
  afterNextRender,
  effect,
  inject,
} from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { A11yModule } from '@angular/cdk/a11y';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

import { FeedbackLinkService } from '../../core/services/feedback-link.service';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { UtilitySheetService } from './utility-sheet.service';

/**
 * The narrow-viewport More sheet: a first-level utility surface that keeps
 * Feedback and Settings one tap below the dock without spending a fifth dock
 * slot or adding another floating control.
 *
 * It deliberately sits on the shell's existing layer ladder — backdrop under
 * drawer, both under the assistant — so an open Ask Nostos surface always keeps
 * the top layer and this sheet can never paint through it.
 */
@Component({
  selector: 'app-utility-sheet',
  standalone: true,
  imports: [RouterLink, NostosIconComponent, A11yModule],
  template: `
    @if (sheet.open()) {
      <div class="utility-sheet-scrim" (click)="sheet.close()" aria-hidden="true"></div>
      <div
        class="utility-sheet"
        role="dialog"
        aria-modal="true"
        aria-label="More"
        [cdkTrapFocus]="true"
        [cdkTrapFocusAutoCapture]="true"
        data-testid="utility-sheet"
      >
        @if (feedbackUrl(); as feedbackHref) {
          <a
            class="utility-sheet-item"
            [href]="feedbackHref"
            target="_blank"
            rel="noopener noreferrer"
            (click)="sheet.close()"
            cdkFocusInitial
            data-testid="utility-sheet-feedback"
          >
            <nostos-icon name="paper-plane-tilt" [size]="18" weight="light"></nostos-icon>
            <span class="utility-sheet-label">Send feedback</span>
            <nostos-icon
              class="utility-sheet-external"
              name="arrow-square-out"
              [size]="13"
            ></nostos-icon>
          </a>
        }

        <a
          class="utility-sheet-item"
          routerLink="/settings"
          (click)="sheet.close()"
          data-testid="utility-sheet-settings"
        >
          <nostos-icon name="gear-six" [size]="18" weight="light"></nostos-icon>
          <span class="utility-sheet-label">Settings</span>
        </a>
      </div>
    }
  `,
  styles: [
    `
      :host {
        display: contents;
      }

      .utility-sheet-scrim {
        position: fixed;
        inset: 0;
        z-index: var(--layer-backdrop);
        background: var(--modal-scrim);
        -webkit-backdrop-filter: blur(var(--modal-scrim-blur));
        backdrop-filter: blur(var(--modal-scrim-blur));
        animation: utility-sheet-fade var(--motion-fast) ease-out both;
      }

      .utility-sheet {
        position: fixed;
        right: 10px;
        bottom: calc(var(--dock-rail-h) + env(safe-area-inset-bottom, 0px) + 10px);
        left: 10px;
        z-index: var(--layer-drawer);
        display: flex;
        flex-direction: column;
        gap: 2px;
        box-sizing: border-box;
        padding: 6px;
        border: 1px solid var(--dock-border);
        border-radius: var(--radius-dock);
        background: var(--dock-surface);
        box-shadow: var(--dock-shadow);
        animation: utility-sheet-enter var(--motion-base) var(--ease-out) both;
      }

      .utility-sheet-item {
        display: flex;
        align-items: center;
        gap: 12px;
        min-height: var(--control-h-touch);
        padding: 0 14px;
        border-radius: calc(var(--radius-dock) - 6px);
        color: var(--color-text-main);
        font-family: 'Hanken Grotesk', sans-serif;
        font-size: 0.92rem;
        font-weight: 500;
        text-decoration: none;
        touch-action: manipulation;
        transition: background-color var(--motion-fast) ease;
      }

      .utility-sheet-item:hover {
        background: var(--bg-hover);
      }

      .utility-sheet-item:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: -2px;
      }

      .utility-sheet-label {
        flex: 1 1 auto;
      }

      .utility-sheet-external {
        color: var(--color-text-muted);
      }

      @keyframes utility-sheet-fade {
        from {
          opacity: 0;
        }
        to {
          opacity: 1;
        }
      }

      @keyframes utility-sheet-enter {
        from {
          opacity: 0;
          transform: translateY(8px);
        }
        to {
          opacity: 1;
          transform: translateY(0);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .utility-sheet-scrim,
        .utility-sheet {
          animation: none;
        }

        .utility-sheet-item {
          transition: none;
        }
      }
    `,
  ],
})
export class UtilitySheetComponent implements OnDestroy {
  readonly sheet = inject(UtilitySheetService);
  readonly feedbackUrl = inject(FeedbackLinkService).url;
  private readonly injector = inject(Injector);

  constructor() {
    // The focus trap captures the More trigger while the shell is still live.
    // Only after that capture does the workspace behind the sheet become inert,
    // so inertness can never take the capture away from CDK (and cannot move
    // focus to body on browsers that blur an inert subtree).
    effect(() => {
      if (!this.sheet.open()) return;
      afterNextRender(
        () => {
          if (this.sheet.open()) this.sheet.backgroundInert.set(true);
        },
        { injector: this.injector },
      );
    });

    // A history navigation while the sheet is open must not leave a modal
    // backdrop over the new surface.
    inject(Router)
      .events.pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.sheet.close());
  }

  ngOnDestroy(): void {
    // This component owns the sheet's inert state; if the workspace itself is
    // destroyed (navigating to the Reader), never leave the next shell inert or
    // the sheet half-open.
    this.sheet.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.sheet.open()) this.sheet.close();
  }
}
