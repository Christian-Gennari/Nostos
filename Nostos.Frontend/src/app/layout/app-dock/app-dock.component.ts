import {
  Component,
  DestroyRef,
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
import { AssistantLauncherService } from '../../ui/assistant/assistant-launcher.service';

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
      <div class="dock-bar" #dockBar [class.has-pending]="pendingDestination() !== null">
        <span class="dock-pill" aria-hidden="true"></span>
        <a
          [routerLink]="getLink('/library')"
          (click)="handleDockClick('/library', $event)"
          routerLinkActive="active"
          #libraryRla="routerLinkActive"
          [class.pending]="pendingDestination() === '/library'"
          class="dock-item"
          title="Library"
        >
          <span class="dock-icon">
            <nostos-icon
              name="books"
              [size]="20"
              [weight]="isSelected('/library', libraryRla.isActive) ? 'fill' : 'regular'"
            ></nostos-icon>
          </span>
          <span class="label">Library</span>
        </a>

        <a
          [routerLink]="getLink('/second-brain')"
          (click)="handleDockClick('/second-brain', $event)"
          routerLinkActive="active"
          #brainRla="routerLinkActive"
          [class.pending]="pendingDestination() === '/second-brain'"
          class="dock-item"
          title="The Brain"
        >
          <span class="dock-icon">
            <nostos-icon
              name="brain"
              [size]="20"
              [weight]="isSelected('/second-brain', brainRla.isActive) ? 'fill' : 'regular'"
            ></nostos-icon>
          </span>
          <span class="label">Brain</span>
        </a>

        @if (isNarrow() && launcher.available()) {
          <!-- On the phone rail Ask Nostos takes the centre slot, between the
               destinations, so it is one thumb-reach from either side. -->
          <button
            type="button"
            class="dock-item dock-item-ask"
            data-assistant-launcher
            data-testid="assistant-trigger"
            (click)="launcher.toggle()"
            aria-haspopup="dialog"
            [attr.aria-expanded]="launcher.isOpen()"
            title="Ask Nostos"
          >
            <span class="dock-icon dock-ask-tile">
              <nostos-icon name="sparkle" [size]="20" weight="fill"></nostos-icon>
            </span>
            <span class="label">Ask</span>
          </button>
        }

        <a
          [routerLink]="getLink('/studio')"
          (click)="handleDockClick('/studio', $event)"
          routerLinkActive="active"
          #studioRla="routerLinkActive"
          [class.pending]="pendingDestination() === '/studio'"
          class="dock-item"
          title="Writing Studio"
        >
          <span class="dock-icon">
            <nostos-icon
              name="pen-nib"
              [size]="20"
              [weight]="isSelected('/studio', studioRla.isActive) ? 'fill' : 'regular'"
            ></nostos-icon>
          </span>
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
              aria-controls="mobile-more-sheet"
              [attr.aria-expanded]="sheet.open()"
              title="More"
              data-testid="dock-more"
            >
              <span class="dock-icon">
                <nostos-icon name="dots-three" [size]="20"></nostos-icon>
              </span>
              <span class="label">More</span>
            </button>
          } @else {
            <a
              routerLink="/settings"
              (click)="handleDockClick('/settings', $event)"
              routerLinkActive="active"
              #settingsNarrowRla="routerLinkActive"
              [class.pending]="pendingDestination() === '/settings'"
              class="dock-item"
              title="Settings"
            >
              <span class="dock-icon">
                <nostos-icon
                  name="gear-six"
                  [size]="20"
                  [weight]="isSelected('/settings', settingsNarrowRla.isActive) ? 'fill' : 'regular'"
                ></nostos-icon>
              </span>
              <span class="label">Settings</span>
            </a>
          }
        } @else {
          @if (launcher.available()) {
            <span class="dock-divider" aria-hidden="true"></span>
            <!-- Looks like a field because it is the way into one: pressing it
                 grows the Ask Nostos panel up out of the dock. -->
            <button
              type="button"
              class="dock-ask"
              data-assistant-launcher
              data-testid="assistant-trigger"
              (click)="launcher.toggle()"
              aria-haspopup="dialog"
              [attr.aria-expanded]="launcher.isOpen()"
              aria-label="Ask Nostos"
            >
              <nostos-icon
                class="dock-ask-glyph"
                name="sparkle"
                [size]="16"
                [weight]="launcher.isOpen() ? 'fill' : 'regular'"
              ></nostos-icon>
              <span class="dock-ask-label">Ask Nostos<span class="dock-ask-ellipsis">…</span></span>
              <kbd class="dock-ask-key" aria-hidden="true">{{ launcher.shortcutLabel }}</kbd>
            </button>
          }
          <!-- Feedback and Settings are utilities, not destinations: one hairline
               sets them apart, and they are icon-only so the dock reads
               Library / Brain / Studio first. The label stays in the DOM as the
               accessible name and the tooltip carries it for the pointer. -->
          <span class="dock-divider" aria-hidden="true"></span>
          @if (feedbackUrl(); as feedbackHref) {
            <a
              class="dock-item dock-item-utility"
              [href]="feedbackHref"
              target="_blank"
              rel="noopener noreferrer"
              title="Send feedback"
              data-testid="dock-feedback"
            >
              <span class="dock-icon">
                <nostos-icon name="paper-plane-tilt" [size]="20"></nostos-icon>
              </span>
              <span class="label">Feedback</span>
            </a>
          }
          <a
            routerLink="/settings"
            (click)="handleDockClick('/settings', $event)"
            routerLinkActive="active"
            #settingsRla="routerLinkActive"
            [class.pending]="pendingDestination() === '/settings'"
            class="dock-item dock-item-utility"
            title="Settings"
          >
            <span class="dock-icon">
              <nostos-icon
                name="gear-six"
                [size]="20"
                [weight]="isSelected('/settings', settingsRla.isActive) ? 'fill' : 'regular'"
              ></nostos-icon>
            </span>
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
        bottom: var(--dock-float-bottom);
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
        gap: 2px;
        /* The bar's total height is the token the Ask Nostos panel opens above,
           so the two can never drift. The 1px borders are outside it. */
        box-sizing: border-box;
        height: calc(var(--dock-bar-h) - 2px);
        padding: 0 4px;
      }

      /* The selected item's pill. It is one element that slides between items
         (positioned by movePill), so a route change reads as motion rather than
         two unrelated repaints. */
      .dock-pill {
        position: absolute;
        top: 4px;
        bottom: 4px;
        left: 0;
        z-index: 0;
        width: 0;
        border-radius: calc(var(--radius-dock) - 5px);
        background: var(--dock-pill);
        opacity: 0;
        pointer-events: none;
        transition:
          transform var(--motion-base) var(--ease-spring),
          width var(--motion-base) var(--ease-spring),
          opacity var(--motion-fast) ease;
      }

      .dock-item {
        position: relative;
        z-index: 1;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 7px;
        height: 38px;
        padding: 0 14px 0 12px;
        border: 0;
        /* Links and the native buttons must paint exactly the same at rest.
           In particular, a button's default ButtonFace is not a dock state. */
        -webkit-appearance: none;
        appearance: none;
        background: transparent;
        /* Concentric with the container: the item sits 5px inside the box edge
           (4px bar inset + 1px border), so the inner corner is the outer one
           minus that gap. */
        border-radius: calc(var(--radius-dock) - 5px);
        color: var(--color-text-muted);
        cursor: pointer;
        font-family: 'Hanken Grotesk', sans-serif;
        font-size: inherit;
        line-height: inherit;
        text-decoration: none;
        white-space: nowrap;
        transition:
          background-color var(--motion-fast) ease,
          color var(--motion-fast) ease,
          transform var(--motion-fast) var(--ease-out);
        touch-action: manipulation;
        -webkit-tap-highlight-color: transparent;
      }

      .dock-icon {
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }

      /* Touch browsers may retain :hover after a tap. More is not a route
         and therefore must not look selected after its sheet closes. */
      @media (hover: hover) {
        .dock-item:hover {
          background: var(--dock-hover);
          color: var(--color-text-main);
        }
      }

      .dock-item:active {
        transform: scale(0.96);
      }

      .dock-item:focus-visible,
      .dock-ask:focus-visible {
        /* Width comes from the shared token like every other ring in the app. The
           COLOUR stays local and deliberately so: an accent-coloured ring
           distinguishes "this dock item" from the neutral slate used everywhere
           else. That is a role difference, not drift. */
        outline: var(--focus-ring-width) solid var(--color-accent);
        outline-offset: -2px;
      }

      /* The selected item: the sliding pill paints behind it, so the item itself
         stays transparent and only its ink changes. RouterLinkActive cannot
         update until navigation commits; a pending item carries the user's
         intent during that gap, and while one exists it outranks the outgoing
         active item. */
      .dock-item.pending,
      .dock-bar:not(.has-pending) .dock-item.active {
        background: transparent;
        color: var(--dock-item-active-ink);
      }

      /* The narrow-viewport More sheet's trigger while it is open. Deliberately
         not the active class: that one also owns the sliding route pill. */
      .dock-item-open {
        background: var(--dock-hover);
        color: var(--dock-item-active-ink);
      }

      .label {
        font-size: 0.8rem;
        font-weight: 600;
        letter-spacing: 0.005em;
        line-height: 1;
      }

      .dock-divider {
        align-self: center;
        flex: none;
        width: 1px;
        height: 22px;
        margin: 0 5px;
        background: var(--dock-border);
      }

      /* Utilities are square, icon-only buttons; the label is kept as the
         accessible name. */
      .dock-item-utility {
        width: 38px;
        padding: 0;
      }

      .dock-item-utility .label {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }

      /* The Ask Nostos launcher: an inset field inside the bar. */
      .dock-ask {
        display: inline-flex;
        flex: none;
        align-items: center;
        gap: 8px;
        width: 236px;
        height: 36px;
        box-sizing: border-box;
        margin: 0 1px;
        padding: 0 8px 0 11px;
        border: 1px solid var(--dock-border);
        -webkit-appearance: none;
        appearance: none;
        border-radius: calc(var(--radius-dock) - 6px);
        background: var(--dock-field);
        color: var(--color-text-muted);
        cursor: text;
        font-family: 'Hanken Grotesk', sans-serif;
        font-size: 0.82rem;
        font-weight: 500;
        line-height: 1;
        text-align: left;
        white-space: nowrap;
        transition:
          border-color var(--motion-fast) ease,
          color var(--motion-fast) ease,
          background-color var(--motion-fast) ease;
        touch-action: manipulation;
      }

      .dock-ask-glyph {
        flex: none;
        color: var(--color-brand-accent);
      }

      .dock-ask-label {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .dock-ask-key {
        flex: none;
        padding: 4px 6px;
        border: 1px solid var(--dock-border);
        border-radius: 6px;
        background: var(--dock-surface);
        color: var(--color-text-muted);
        font-family: inherit;
        font-size: 0.66rem;
        font-weight: 600;
        letter-spacing: 0.03em;
        line-height: 1;
      }

      @media (hover: hover) {
        .dock-ask:hover {
          border-color: var(--control-border-hover);
          color: var(--color-text-main);
        }
      }

      .dock-ask[aria-expanded='true'] {
        border-color: var(--color-brand-accent);
        background: var(--dock-surface);
        color: var(--color-text-main);
      }

      /* Mid widths (tablets, split windows): the dock gives its width back.
         Unselected destinations go icon-only (the tooltip still names them), the
         selected one keeps its label, and the Ask field becomes a short pill. */
      @media (max-width: 1199px) and (min-width: 769px) {
        .dock-item:not(.dock-item-utility) {
          padding: 0 11px;
        }

        .dock-bar.has-pending .dock-item:not(.pending) .label,
        .dock-bar:not(.has-pending) .dock-item:not(.active) .label {
          display: none;
        }

        .dock-ask {
          width: auto;
          padding: 0 13px 0 11px;
        }

        .dock-ask-ellipsis,
        .dock-ask-key {
          display: none;
        }
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
             remaining 1px border. The inset is part of the height on a notched
             phone, matching the shell's reserve. */
          box-sizing: border-box;
          height: calc(var(--dock-rail-h) + env(safe-area-inset-bottom, 0px));
        }

        .dock-bar {
          width: 100%;
          justify-content: space-around;
          gap: 0;
          box-sizing: border-box;
          height: 100%;
          padding: 0 6px env(safe-area-inset-bottom, 0px);
        }

        /* On the rail the selected marker is a pill around the icon, not a
           slab behind the whole item: five items share 390px, so a slab
           would touch its neighbours. */
        .dock-pill {
          display: none;
        }

        .dock-item {
          min-width: 0;
          height: 100%;
          min-height: 44px;
          flex: 1;
          flex-direction: column;
          gap: 3px;
          padding: 0;
          border-radius: 0;
        }

        .dock-icon {
          width: 52px;
          height: 30px;
          border-radius: 999px;
          transition:
            background-color var(--motion-base) var(--ease-out),
            color var(--motion-fast) ease;
        }

        .dock-item.pending .dock-icon,
        .dock-bar:not(.has-pending) .dock-item.active .dock-icon,
        .dock-item-open .dock-icon {
          background: var(--dock-pill);
        }

        .dock-item-open {
          background: transparent;
        }

        /* The hover wash belongs to the pill on touch, not the whole column. */
        .dock-item:hover {
          background: transparent;
        }

        .dock-item:active {
          transform: none;
        }

        .dock-item:active .dock-icon {
          transform: scale(0.94);
        }

        .label {
          /* 0.7rem is the floor: below ~11px the labels stop being readable at
             arm's length (mobile-dock.spec). */
          font-size: 0.7rem;
          font-weight: 500;
          letter-spacing: 0.01em;
        }

        .dock-item.pending .label,
        .dock-bar:not(.has-pending) .dock-item.active .label {
          font-weight: 650;
        }

        /* Ask Nostos is an action, not a place: a filled tile in the CTA ink,
           the same shape as the selected pill so the rail stays one system. */
        .dock-ask-tile {
          background: var(--cta-surface);
          color: var(--cta-ink);
          box-shadow: var(--button-shadow-primary);
        }

        .dock-item-ask {
          color: var(--color-text-main);
        }

        .dock-item-ask .label {
          font-weight: 650;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        :host,
        .dock-item,
        .dock-icon,
        .dock-ask,
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
  readonly launcher = inject(AssistantLauncherService);

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
    // While the dock is on screen it carries the Ask Nostos launcher, so the
    // assistant's own floating capsule stands down.
    const detachLauncher = this.launcher.attachDock();
    let resizeObserver: ResizeObserver | null = null;
    inject(DestroyRef).onDestroy(() => {
      detachLauncher();
      resizeObserver?.disconnect();
    });

    afterNextRender(() => {
      this.movePill();
      // The bar's contents change width without a route change: the Ask
      // launcher and Feedback appear once their answers arrive, and the
      // webfont settles after first paint. Re-measure whenever the bar does.
      const bar = this.dockBar()?.nativeElement;
      if (bar && typeof ResizeObserver !== 'undefined') {
        resizeObserver = new ResizeObserver(() => this.movePill());
        resizeObserver.observe(bar);
      }
    });
  }

  /** The item that reads as selected: the pending one, else the active route. */
  isSelected(prefix: string, routeActive: boolean): boolean {
    const pending = this.pendingDestination();
    return pending !== null ? pending === prefix : routeActive;
  }

  @HostListener('window:resize')
  onResize(): void {
    const narrow = isNarrowViewport();
    this.isNarrow.set(narrow);
    // Crossing to the wide shell removes the More trigger, so the narrow sheet
    // must not survive the breakpoint. CDK's trap restores focus only if the
    // trigger still exists; a detached trigger is a safe no-op.
    if (!narrow) this.sheet.close();
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
      pill.style.width = `${itemRect.width}px`;
      pill.style.transform = `translateX(${itemRect.left - barRect.left}px)`;
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
