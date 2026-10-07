import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  HostListener,
  Injector,
  OnDestroy,
  afterNextRender,
  effect,
  inject,
  signal,
} from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { A11yModule } from '@angular/cdk/a11y';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

import { FeedbackLinkService } from '../../core/services/feedback-link.service';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { UtilitySheetService } from './utility-sheet.service';

/**
 * The narrow-viewport More surface: a compact first-level utility menu that
 * keeps Feedback and Settings one tap below the dock without spending a fifth
 * dock slot or adding another floating control.
 *
 * It deliberately sits on the shell's existing layer ladder — backdrop under
 * drawer, both under the assistant — so an open Ask Nostos surface always keeps
 * the top layer and this menu can never paint through it.
 */
@Component({
  selector: 'app-utility-sheet',
  standalone: true,
  imports: [RouterLink, NostosIconComponent, A11yModule],
  template: `
    @if (rendered()) {
      <div
        class="utility-sheet-scrim"
        [class.is-open]="sheet.open()"
        (click)="sheet.close()"
        aria-hidden="true"
      ></div>
      <div
        class="utility-sheet"
        id="mobile-more-sheet"
        [class.is-open]="sheet.open()"
        [style.--utility-sheet-right]="sheetRight() + 'px'"
        role="dialog"
        [attr.aria-modal]="sheet.open() ? 'true' : null"
        [attr.aria-hidden]="sheet.open() ? null : 'true'"
        [attr.inert]="sheet.open() ? null : ''"
        aria-label="More"
        [cdkTrapFocus]="sheet.open()"
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
        opacity: 0;
        transition: opacity var(--motion-fast) ease-out;
      }

      .utility-sheet-scrim.is-open {
        opacity: 0.14;
      }

      .utility-sheet-scrim:not(.is-open) {
        pointer-events: none;
      }

      .utility-sheet {
        --utility-sheet-right: 12px;
        position: fixed;
        right: max(env(safe-area-inset-right, 0px), var(--utility-sheet-right));
        bottom: calc(var(--dock-rail-h) + env(safe-area-inset-bottom, 0px) + 8px);
        z-index: var(--layer-drawer);
        display: flex;
        width: min(
          224px,
          calc(
            100vw - env(safe-area-inset-left, 0px) - env(safe-area-inset-right, 0px) - 24px
          )
        );
        flex-direction: column;
        gap: 2px;
        box-sizing: border-box;
        padding: 6px;
        border: 1px solid var(--dock-border);
        border-radius: var(--radius-dock);
        background: var(--dock-surface);
        box-shadow: var(--dock-shadow);
        opacity: 0;
        transform: translateY(6px) scale(0.985);
        transform-origin: bottom right;
        pointer-events: none;
        transition:
          opacity var(--motion-base) var(--ease-out),
          transform var(--motion-base) var(--ease-out);
      }

      .utility-sheet.is-open {
        opacity: 1;
        transform: translateY(0) scale(1);
        pointer-events: auto;
      }

      @starting-style {
        .utility-sheet-scrim.is-open {
          opacity: 0;
        }

        .utility-sheet.is-open {
          opacity: 0;
          transform: translateY(8px) scale(0.985);
        }
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

      @media (prefers-reduced-motion: reduce) {
        .utility-sheet-scrim,
        .utility-sheet {
          transition: none;
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
  readonly rendered = signal(false);
  readonly sheetRight = signal(12);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private returnFocus: HTMLElement | null = null;
  private exitTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // Keep the panel mounted for its short exit transition, but release focus
    // and make it inert as soon as the menu closes. Focus is captured manually
    // on every open because the trap remains mounted briefly during an exit.
    effect(() => {
      if (this.sheet.open()) {
        if (this.exitTimer !== undefined) clearTimeout(this.exitTimer);
        this.exitTimer = undefined;
        this.returnFocus = this.focusable(this.document.activeElement);
        this.rendered.set(true);

        afterNextRender(() => {
          if (!this.sheet.open()) return;
          this.syncPosition();
          const firstItem = (this.host.nativeElement as HTMLElement).querySelector(
            '[cdkFocusInitial], .utility-sheet-item',
          ) as HTMLElement | null;
          firstItem?.focus();
          // The dock stays live until its focus has moved into the menu.
          this.sheet.backgroundInert.set(true);
        }, { injector: this.injector });
        return;
      }

      if (!this.rendered()) return;

      this.sheet.backgroundInert.set(false);
      if (this.returnFocus?.isConnected) this.returnFocus.focus();
      this.returnFocus = null;
      this.exitTimer = setTimeout(() => {
        this.rendered.set(false);
        this.exitTimer = undefined;
      }, this.motionDuration());
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
    if (this.exitTimer !== undefined) clearTimeout(this.exitTimer);
    this.sheet.close();
  }

  @HostListener('window:resize')
  onResize(): void {
    if (this.sheet.open()) this.syncPosition();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.sheet.open()) this.sheet.close();
  }

  private syncPosition(): void {
    const trigger = this.document.querySelector<HTMLElement>('[data-testid="dock-more"]');
    const viewportWidth = this.document.defaultView?.innerWidth;
    if (!trigger || !viewportWidth) return;

    this.sheetRight.set(Math.max(0, viewportWidth - trigger.getBoundingClientRect().right));
  }

  private focusable(element: Element | null): HTMLElement | null {
    return element && 'focus' in element ? (element as HTMLElement) : null;
  }

  private motionDuration(): number {
    if (this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
      return 0;
    }

    const token = this.document.defaultView
      ?.getComputedStyle(this.document.documentElement)
      .getPropertyValue('--motion-base')
      .trim();
    const value = Number.parseFloat(token ?? '');
    if (!Number.isFinite(value)) return 220;
    return value * (token?.endsWith('ms') ? 1 : 1000);
  }
}
