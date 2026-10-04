import {
  Component,
  ElementRef,
  HostListener,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  Router,
  RouterLink,
  RouterLinkActive,
  type UrlTree,
} from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationHistoryService } from '../../core/services/navigation-history.service';
import { FeedbackLinkService } from '../../core/services/feedback-link.service';
import { LibraryFilterService } from '../../library/library-filter.service';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { UtilitySheetService } from '../utility-sheet/utility-sheet.service';

/** The phone rail's breakpoint, matching the dock's own stylesheet. */
function isNarrowViewport(): boolean {
  if (typeof window === 'undefined') return false;
  if (typeof window.matchMedia === 'function') {
    return window.matchMedia('(max-width: 768px)').matches;
  }
  return window.innerWidth <= 768;
}

@Component({
  standalone: true,
  selector: 'app-app-dock',
  imports: [RouterLink, RouterLinkActive, NostosIconComponent],
  template: `
    <nav class="app-dock-container" aria-label="Main navigation">
      <div class="dock-bar" #dockBar>
        <span class="dock-pill" aria-hidden="true"></span>
        <a
          [routerLink]="getLink('/library')"
          (click)="handleDockClick('/library', $event)"
          routerLinkActive="active"
          [class.pending]="pendingDestination() === '/library'"
          class="dock-item"
          title="Library"
        >
          <nostos-icon name="books" [size]="20" weight="light"></nostos-icon>
          <span class="label">Library</span>
        </a>

        <a
          [routerLink]="getLink('/second-brain')"
          (click)="handleDockClick('/second-brain', $event)"
          routerLinkActive="active"
          [class.pending]="pendingDestination() === '/second-brain'"
          class="dock-item"
          title="The Brain"
        >
          <nostos-icon name="brain" [size]="20" weight="light"></nostos-icon>
          <span class="label">Brain</span>
        </a>

        <a
          [routerLink]="getLink('/studio')"
          (click)="handleDockClick('/studio', $event)"
          routerLinkActive="active"
          [class.pending]="pendingDestination() === '/studio'"
          class="dock-item"
          title="Writing Studio"
        >
          <nostos-icon name="pen-nib" [size]="20" weight="light"></nostos-icon>
          <span class="label">Studio</span>
        </a>

        @if (isNarrow()) {
          @if (feedbackUrl()) {
            <button
              type="button"
              class="dock-item"
              [class.dock-item-open]="sheet.open()"
              (click)="sheet.toggle()"
              aria-haspopup="dialog"
              [attr.aria-expanded]="sheet.open()"
              title="More"
              data-testid="dock-more"
            >
              <nostos-icon name="dots-three" [size]="20" weight="light"></nostos-icon>
              <span class="label">More</span>
            </button>
          } @else {
            <a
              routerLink="/settings"
              (click)="handleDockClick('/settings', $event)"
              routerLinkActive="active"
              [class.pending]="pendingDestination() === '/settings'"
              class="dock-item"
              title="Settings"
            >
              <nostos-icon name="gear-six" [size]="20" weight="light"></nostos-icon>
              <span class="label">Settings</span>
            </a>
          }
        } @else {
          @if (feedbackUrl(); as feedbackHref) {
            <!-- One hairline sets the utility pair apart from the primary
                 destinations; without Feedback (SelfHosted) the dock is
                 unchanged and Settings stays where it always was. -->
            <span class="dock-divider" aria-hidden="true"></span>
            <a
              class="dock-item dock-item-utility"
              [href]="feedbackHref"
              target="_blank"
              rel="noopener noreferrer"
              title="Send feedback"
              aria-label="Send feedback"
              data-testid="dock-feedback"
            >
              <nostos-icon name="paper-plane-tilt" [size]="20" weight="light"></nostos-icon>
              <span class="label">Feedback</span>
            </a>
          }
          <a
            routerLink="/settings"
            (click)="handleDockClick('/settings', $event)"
            routerLinkActive="active"
            [class.pending]="pendingDestination() === '/settings'"
            class="dock-item"
            [class.dock-item-utility]="!!feedbackUrl()"
            title="Settings"
          >
            <nostos-icon name="gear-six" [size]="20" weight="light"></nostos-icon>
            <span class="label">Settings</span>
          </a>
        }
      </div>
    </nav>
  `,
  styles: [
    `
      :host {
        position: fixed;
        bottom: 24px;
        left: 50%;
        z-index: var(--layer-navigation);
        width: auto;
        padding: 0;
        transform: translateX(-50%);
      }

      .app-dock-container {
        width: max-content;
        /* The --dock-* tokens: on light the dock floats on a warm cast; on dark a
           dark shadow carries no elevation, so the surface itself lifts (measured
           1.55 tonal separation from the page). One rule, both themes.
           NOTE: no backticks in this file - these styles live in a template
           literal, and a backtick terminates it. */
        border: 1px solid var(--dock-border);
        border-radius: var(--radius-dock);
        overflow: clip;
        background: var(--dock-surface);
        box-shadow: var(--dock-shadow);
      }

      .dock-bar {
        position: relative;
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 2px;
        padding: 3px 6px;
      }

      .dock-pill {
        position: absolute;
        bottom: 3px;
        left: 0;
        z-index: 1;
        height: 2px;
        width: 0;
        background: var(--color-brand-accent);
        opacity: 0;
        pointer-events: none;
        transition:
          transform var(--motion-base) var(--ease-spring),
          width var(--motion-base) var(--ease-spring),
          opacity var(--motion-fast) ease;
      }

      .dock-item {
        position: relative;
        display: inline-flex;
        min-width: 78px;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 4px;
        padding: 8px 12px 7px;
        border: 0;
        border-bottom: 2px solid transparent;
        /* Concentric with the container: the bar's 3px padding is the gap
           between the item box and the box edge, so the inner corner is the
           outer one minus that gap. Without this the hover wash reads as a
           square tile nested in a round box. */
        border-radius: calc(var(--radius-dock) - 3px);
        color: var(--color-text-muted);
        cursor: pointer;
        font-family: 'Hanken Grotesk', sans-serif;
        text-decoration: none;
        transition:
          background-color var(--motion-fast) ease,
          color var(--motion-fast) ease,
          transform var(--motion-fast) var(--ease-out);
        touch-action: manipulation;
      }

      .dock-item:hover {
        background: var(--bg-hover);
        color: var(--color-text-main);
      }

      .dock-item:active {
        transform: scale(0.96);
      }

      .dock-item:focus-visible {
        /* Width comes from the shared token like every other ring in the app. The
           COLOUR stays local and deliberately so: this dock's items include
           destructive actions, and an accent-coloured ring distinguishes "this dock
           item" from the neutral slate used everywhere else. That is a role
           difference, not drift — the drift was the hardcoded width. */
        outline: var(--focus-ring-width) solid var(--color-accent);
        outline-offset: -2px;
      }

      .dock-item.active {
        border-bottom-color: transparent;
        background: transparent;
        /* Ink on light; porcelain on dark, matching the heading rule. */
        color: var(--dock-item-active-ink);
      }

      /* RouterLinkActive cannot update until navigation commits. A pending item
         carries the user's intent during that gap, so touch never returns to an
         inert-looking dock between release and NavigationEnd. */
      .dock-item.pending {
        background: var(--bg-hover);
        color: var(--dock-item-active-ink);
      }

      /* The narrow-viewport More sheet's trigger while it is open. Deliberately
         not the active class: that one also owns the sliding route pill. */
      .dock-item-open {
        background: var(--bg-hover);
        color: var(--dock-item-active-ink);
      }

      /* Feedback and Settings are utilities, not destinations: one hairline
         separates them from the primary trio, and their labels sit a touch
         lighter so the dock still reads Library/Brain/Studio first. */
      .dock-divider {
        align-self: stretch;
        width: 1px;
        margin: 7px 5px;
        background: var(--dock-border);
      }

      .dock-item-utility {
        min-width: 72px;
      }

      .dock-item-utility .label {
        font-size: 0.64rem;
        font-weight: 500;
      }

      .label {
        font-size: 0.68rem;
        font-weight: 600;
        letter-spacing: 0.015em;
        line-height: 1;
      }

      @media (max-width: 768px) {
        :host {
          right: 0;
          bottom: 0;
          left: 0;
          width: 100%;
          transform: none;
        }

        .app-dock-container {
          /* The desktop dock is a floating pill sized to its content
             (width: max-content). On phones the dock is a full-bleed rail, so
             the container must stretch, otherwise it collapses to a narrow
             left-aligned pill with sub-44px tap targets. */
          width: 100%;
          max-width: none;
          border-right: 0;
          border-bottom: 0;
          border-left: 0;
          border-radius: 0;
          /* The phone dock is a full-bleed rail, so its cast points UP. */
          box-shadow: var(--dock-shadow-rail);
          /* The rail's TOTAL height is the token the shell reserves for it, and it
             is set here rather than on the inner bar because this element owns the
             remaining 1px border: sizing the bar left the dock 1px taller than the
             reserve, which put it 1px over the last line of content. The inset is
             part of the height on a notched phone, matching the shell's reserve.
             "height" not "min-height": a floor lets natural content win. */
          box-sizing: border-box;
          height: calc(var(--dock-rail-h) + env(safe-area-inset-bottom, 0px));
        }

        .dock-bar {
          width: 100%;
          justify-content: space-around;
          gap: 1px;
          /* Fill the container, whose height is the reserved --dock-rail-h.
             Content clearance and dock height are therefore one value rather than
             two that can drift (they were 96px and 58px, leaving 38px of dead space
             above the dock on every page). */
          box-sizing: border-box;
          height: 100%;
          padding: 3px 10px max(3px, env(safe-area-inset-bottom));
        }

        .dock-item {
          min-width: 0;
          min-height: 44px;
          flex: 1;
          gap: 2px;
          padding: 5px 4px 6px;
          /* The phone rail is a full-bleed rectangle with square corners, so
             there is no outer curve for these to be concentric with; keep the
             app's default small radius (this is the mobile dock as it shipped). */
          border-radius: var(--radius-sm);
        }

        .dock-item nostos-icon {
          /* 20px icons: the desktop 0.9 scale (18px) is too small to read or
             hit comfortably on a phone. */
          transform: none;
        }

        .label {
          font-size: 0.7rem;
        }

        .dock-item:hover {
          transform: none;
        }

        .dock-item:active {
          transform: scale(0.96);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        :host,
        .dock-item,
        .dock-pill {
          transition: none;
        }
      }
    `,
  ],
})
export class AppDockComponent {
  private historyService = inject(NavigationHistoryService);
  private router = inject(Router);
  private filters = inject(LibraryFilterService);
  private dockBar = viewChild<ElementRef<HTMLElement>>('dockBar');

  readonly sheet = inject(UtilitySheetService);
  readonly feedbackUrl = inject(FeedbackLinkService).url;

  /** Destination chosen by the pointer but not yet committed by the router. */
  readonly pendingDestination = signal<string | null>(null);

  /** The dock's own breakpoint, because the two branches render different DOM. */
  readonly isNarrow = signal(isNarrowViewport());

  constructor() {
    this.router.events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (
        event instanceof NavigationEnd ||
        event instanceof NavigationCancel ||
        event instanceof NavigationError
      ) {
        this.pendingDestination.set(null);
        this.movePill();
      }
    });
    afterNextRender(() => this.movePill());
  }

  @HostListener('window:resize')
  onResize(): void {
    this.isNarrow.set(isNarrowViewport());
    this.movePill();
  }

  private movePill(): void {
    // routerLinkActive settles on the next frame; measure after that so the
    // pill never chases a stale item.
    requestAnimationFrame(() => {
      const bar = this.dockBar()?.nativeElement;
      const pill = bar?.querySelector<HTMLElement>('.dock-pill');
      if (!bar || !pill) return;
      const active =
        bar.querySelector<HTMLElement>('.dock-item.pending') ??
        bar.querySelector<HTMLElement>('.dock-item.active');
      if (!active) {
        pill.style.opacity = '0';
        return;
      }
      const barRect = bar.getBoundingClientRect();
      const itemRect = active.getBoundingClientRect();
      pill.style.opacity = '1';
      pill.style.width = `${Math.max(0, itemRect.width - 24)}px`;
      pill.style.transform = `translateX(${itemRect.left - barRect.left + 12}px)`;
    });
  }

  getLink(prefix: string): UrlTree {
    // History stores the complete URL so Brain can restore evidence deep-links
    // such as /second-brain?topicId=.... RouterLink interprets a plain string
    // as navigation commands, so parse the remembered URL first to preserve its
    // path, query params and fragment as URL structure.
    return this.router.parseUrl(this.historyService.getLastUrl(prefix));
  }

  handleDockClick(prefix: string, event: Event) {
    if (this.router.url.startsWith(prefix)) {
      event.preventDefault();
      this.pendingDestination.set(null);
      // Re-clicking the Library dock item clears the active filters.
      if (prefix === '/library') this.filters.clearAll();
      return;
    }

    // A click event runs before RouterLink starts navigation. Paint the intended
    // destination now; RouterLinkActive becomes authoritative on completion.
    this.pendingDestination.set(prefix);
    this.movePill();
  }
}
