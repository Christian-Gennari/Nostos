/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName } from '../icon/nostos-icons';

export type DropdownSize = 'normal' | 'compact';
export type DropdownAlignment = 'start' | 'end';
export type DropdownAppearance = 'field' | 'subtle';

export interface DropdownOption {
  readonly value: string;
  readonly label: string;
  readonly disabled?: boolean;
  readonly icon?: NostosIconName;
}

let nextDropdownId = 0;

/**
 * Canonical Nostos single-choice dropdown.
 *
 * Focus stays on the trigger while the listbox is open and the active option is
 * exposed through aria-activedescendant. The panel uses the browser top layer
 * when Popover is available so dialogs and constrained product surfaces do not
 * clip it. Product-owned action menus/listboxes with different semantics stay local.
 */
@Component({
  selector: 'app-dropdown',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'nostos-dropdown',
    '[class.nostos-dropdown--compact]': "controlSize() === 'compact'",
    '[class.nostos-dropdown--subtle]': "appearance() === 'subtle'",
    '[class.nostos-dropdown--full]': 'fullWidth()',
    '[class.nostos-dropdown--disabled]': 'disabled()',
  },
  template: `
    <button
      #trigger
      type="button"
      class="nostos-dropdown__trigger"
      role="combobox"
      aria-haspopup="listbox"
      [attr.id]="controlId()"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="listboxId"
      [attr.aria-activedescendant]="activeDescendant()"
      [attr.aria-invalid]="invalid() ? 'true' : null"
      [disabled]="disabled()"
      (click)="toggle()"
      (keydown)="onTriggerKeydown($event)"
    >
      <span class="nostos-dropdown__value">{{ selectedOption()?.label ?? placeholder() }}</span>
      <nostos-icon
        class="nostos-dropdown__chevron"
        name="caret-down"
        [size]="12"
        [class.nostos-dropdown__chevron--open]="open()"
      />
    </button>

    <div
      #panel
      class="nostos-dropdown__panel"
      [class.nostos-dropdown__panel--fallback-open]="fallbackOpen()"
      popover="manual"
      role="listbox"
      [id]="listboxId"
      [attr.aria-label]="ariaLabel()"
      (pointerdown)="$event.stopPropagation()"
    >
      @for (option of options(); track option.value; let index = $index) {
        <div
          class="nostos-dropdown__option"
          role="option"
          [id]="optionId(index)"
          [class.nostos-dropdown__option--active]="activeIndex() === index"
          [class.nostos-dropdown__option--selected]="option.value === value()"
          [class.nostos-dropdown__option--disabled]="option.disabled"
          [attr.aria-selected]="option.value === value()"
          [attr.aria-disabled]="option.disabled ? 'true' : null"
          (mouseenter)="activate(index)"
          (click)="selectOption(option)"
        >
          @if (option.icon; as icon) {
            <nostos-icon class="nostos-dropdown__option-icon" [name]="icon" [size]="15" />
          }
          <span class="nostos-dropdown__option-label">{{ option.label }}</span>
          @if (option.value === value()) {
            <nostos-icon class="nostos-dropdown__check" name="check" [size]="14" />
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: inline-block;
        min-width: 0;
        max-width: 100%;
        color: var(--color-text-main);
      }

      :host(.nostos-dropdown--full) {
        display: block;
        width: 100%;
      }

      .nostos-dropdown__trigger {
        box-sizing: border-box;
        display: inline-flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
        width: 100%;
        min-width: 7.5rem;
        min-height: var(--control-h-touch);
        padding: 0.75rem 0.85rem;
        border: 1px solid var(--dropdown-border, var(--control-border));
        border-radius: var(--radius-md);
        background: var(--dropdown-bg, var(--bg-input));
        color: inherit;
        font: inherit;
        font-size: 0.95rem;
        line-height: 1.35;
        text-align: left;
        cursor: pointer;
        outline: var(--focus-ring-width) solid transparent;
        outline-offset: 1px;
        touch-action: manipulation;
        transition:
          border-color var(--motion-fast) var(--ease-plain),
          outline-color var(--motion-fast) var(--ease-plain),
          background-color var(--motion-fast) var(--ease-plain);
      }

      :host(.nostos-dropdown--compact) .nostos-dropdown__trigger {
        min-height: var(--dropdown-compact-height, 40px);
        padding: var(--dropdown-compact-padding, 0.65rem 0.8rem);
        font-size: 0.9rem;
      }

      @media (pointer: coarse), (hover: none) {
        :host(.nostos-dropdown--compact) .nostos-dropdown__trigger {
          min-height: var(--control-h-touch);
        }
      }

      /* Low-chrome toolbar controls still read as controls through their label,
         caret and hover/focus behaviour, so they use the app hairline instead of
         the stronger form-field boundary. The popup itself is unchanged. */
      :host(.nostos-dropdown--subtle) {
        --dropdown-border: var(--border-color);
        --dropdown-bg: transparent;
        --dropdown-compact-height: 36px;
        --dropdown-compact-padding: 0.5rem 0.7rem;
      }

      .nostos-dropdown__trigger:hover:not(:disabled) {
        background: var(--bg-hover);
      }

      .nostos-dropdown__trigger:focus-visible {
        border-color: var(--border-focus);
        outline-color: var(--focus-ring);
      }

      .nostos-dropdown__trigger[aria-invalid='true'] {
        border-color: var(--color-danger);
      }

      .nostos-dropdown__trigger[aria-invalid='true']:focus-visible {
        outline-color: var(--color-danger);
      }

      .nostos-dropdown__trigger:disabled {
        background: var(--bg-surface-alt);
        color: var(--color-text-light);
        cursor: not-allowed;
      }

      .nostos-dropdown__value {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .nostos-dropdown__chevron {
        flex: 0 0 auto;
        color: var(--color-text-muted);
        transition: transform var(--motion-fast) var(--ease-plain);
      }

      .nostos-dropdown__chevron--open {
        transform: rotate(180deg);
      }

      .nostos-dropdown__panel {
        position: fixed;
        inset: auto;
        box-sizing: border-box;
        display: none;
        width: max-content;
        min-width: 7.5rem;
        max-width: calc(100vw - 16px);
        max-height: min(20rem, calc(100vh - 16px));
        margin: 0;
        padding: 4px;
        overflow: auto;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-md);
        background: var(--bg-surface);
        color: var(--color-text-main);
        box-shadow: var(--shadow-md);
        visibility: hidden;
      }

      .nostos-dropdown__panel:popover-open,
      .nostos-dropdown__panel--fallback-open {
        display: block;
        animation: nostos-dropdown-enter var(--motion-fast) var(--ease-plain);
      }

      .nostos-dropdown__option {
        box-sizing: border-box;
        display: flex;
        align-items: center;
        gap: 0.55rem;
        width: 100%;
        min-height: 36px;
        padding: 0.48rem 0.65rem;
        border-radius: var(--radius-sm);
        color: var(--color-text-main);
        font-size: 0.88rem;
        line-height: 1.3;
        white-space: nowrap;
        cursor: pointer;
        user-select: none;
      }

      .nostos-dropdown__option--active:not(.nostos-dropdown__option--disabled) {
        background: var(--bg-hover);
      }

      .nostos-dropdown__option--selected {
        background: var(--control-active-fill);
        color: var(--control-active-ink);
        font-weight: var(--fw-medium);
      }

      .nostos-dropdown__option--active.nostos-dropdown__option--selected {
        background: var(--control-active-fill);
      }

      .nostos-dropdown__option--disabled {
        color: var(--color-text-light);
        cursor: not-allowed;
        opacity: 0.62;
      }

      .nostos-dropdown__option-icon,
      .nostos-dropdown__check {
        flex: 0 0 auto;
      }

      .nostos-dropdown__option-label {
        flex: 1 1 auto;
        min-width: 0;
      }

      .nostos-dropdown__check {
        margin-left: auto;
      }

      @keyframes nostos-dropdown-enter {
        from {
          opacity: 0;
          transform: translateY(-3px);
        }
        to {
          opacity: 1;
          transform: translateY(0);
        }
      }

      @media (max-width: 768px) {
        :host(.nostos-dropdown--compact) .nostos-dropdown__trigger {
          min-height: var(--control-h-touch);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .nostos-dropdown__trigger,
        .nostos-dropdown__chevron {
          transition: none;
        }

        .nostos-dropdown__panel:popover-open,
        .nostos-dropdown__panel--fallback-open {
          animation: none;
        }
      }
    `,
  ],
})
export class DropdownComponent {
  readonly options = input<readonly DropdownOption[]>([]);
  readonly value = input<string | null>(null);
  readonly valueChange = output<string>();
  readonly placeholder = input('Select…');
  readonly ariaLabel = input<string | null>(null);
  readonly controlId = input<string | null>(null);
  readonly controlSize = input<DropdownSize>('normal');
  readonly appearance = input<DropdownAppearance>('field');
  readonly align = input<DropdownAlignment>('start');
  readonly fullWidth = input(false);
  readonly disabled = input(false);
  readonly invalid = input(false);

  readonly open = signal(false);
  readonly activeIndex = signal(-1);
  readonly fallbackOpen = signal(false);

  readonly selectedOption = computed(
    () => this.options().find((option) => option.value === this.value()) ?? null,
  );

  readonly activeDescendant = computed(() => {
    const index = this.activeIndex();
    return this.open() && index >= 0 ? this.optionId(index) : null;
  });

  readonly listboxId = `nostos-dropdown-${++nextDropdownId}-listbox`;

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  private readonly panel = viewChild.required<ElementRef<HTMLDivElement>>('panel');

  toggle(): void {
    if (this.disabled()) return;
    this.open() ? this.close(false) : this.showPanel();
  }

  onTriggerKeydown(event: KeyboardEvent): void {
    if (this.disabled()) return;

    if (event.key === 'Escape' && this.open()) {
      event.preventDefault();
      this.close(true);
      return;
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const direction = event.key === 'ArrowDown' ? 1 : -1;
      if (!this.open()) this.showPanel();
      this.moveActive(direction);
      return;
    }

    if ((event.key === 'Home' || event.key === 'End') && this.open()) {
      event.preventDefault();
      this.setKeyboardActive(this.firstEnabledIndex(event.key === 'End'));
      return;
    }

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      if (!this.open()) {
        this.showPanel();
        return;
      }

      const option = this.options()[this.activeIndex()];
      if (option) this.selectOption(option);
      return;
    }

    if (event.key === 'Tab' && this.open()) this.close(false);
  }

  activate(index: number): void {
    if (!this.options()[index]?.disabled) this.activeIndex.set(index);
  }

  selectOption(option: DropdownOption): void {
    if (option.disabled) return;
    if (option.value !== this.value()) this.valueChange.emit(option.value);
    this.close(true);
  }

  optionId(index: number): string {
    return `${this.listboxId}-option-${index}`;
  }

  @HostListener('document:pointerdown', ['$event'])
  onDocumentPointerDown(event: PointerEvent): void {
    if (!this.open()) return;
    const target = event.target;
    if (target instanceof Node && !this.host.nativeElement.contains(target)) this.close(false);
  }

  @HostListener('window:resize')
  @HostListener('window:scroll')
  onViewportChange(): void {
    if (this.open()) this.positionPanel();
  }

  private showPanel(): void {
    if (this.disabled() || !this.options().some((option) => !option.disabled)) return;

    const selectedIndex = this.options().findIndex(
      (option) => option.value === this.value() && !option.disabled,
    );
    this.setKeyboardActive(
      selectedIndex >= 0 ? selectedIndex : this.firstEnabledIndex(false),
    );
    this.open.set(true);

    const panel = this.panel().nativeElement;
    panel.style.visibility = 'hidden';
    const popover = panel as HTMLDivElement & {
      showPopover?: () => void;
      hidePopover?: () => void;
    };

    if (typeof popover.showPopover === 'function') {
      this.fallbackOpen.set(false);
      try {
        popover.showPopover();
      } catch {
        this.fallbackOpen.set(true);
      }
    } else {
      this.fallbackOpen.set(true);
    }

    this.positionPanel();
    panel.style.visibility = 'visible';
  }

  private close(restoreFocus: boolean): void {
    if (!this.open()) return;

    const panel = this.panel().nativeElement;
    const popover = panel as HTMLDivElement & { hidePopover?: () => void };
    if (!this.fallbackOpen() && typeof popover.hidePopover === 'function') {
      try {
        popover.hidePopover();
      } catch {
        // The panel may already have been dismissed by the browser.
      }
    }

    panel.style.visibility = 'hidden';
    this.fallbackOpen.set(false);
    this.open.set(false);
    this.activeIndex.set(-1);

    if (restoreFocus) queueMicrotask(() => this.trigger().nativeElement.focus());
  }

  private moveActive(direction: 1 | -1): void {
    const options = this.options();
    if (!options.length) return;

    let index = this.activeIndex();
    for (let attempt = 0; attempt < options.length; attempt++) {
      index = (index + direction + options.length) % options.length;
      if (!options[index].disabled) {
        this.setKeyboardActive(index);
        return;
      }
    }
  }

  private setKeyboardActive(index: number): void {
    this.activeIndex.set(index);
    if (index < 0) return;

    // Focus deliberately stays on the combobox trigger. Keep the active
    // descendant visible when keyboard navigation moves through a long list.
    queueMicrotask(() => {
      const option = this.panel().nativeElement.querySelector<HTMLElement>(
        `#${this.optionId(index)}`,
      );
      option?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  private firstEnabledIndex(fromEnd: boolean): number {
    const options = this.options();
    if (fromEnd) {
      for (let index = options.length - 1; index >= 0; index--) {
        if (!options[index].disabled) return index;
      }
      return -1;
    }

    return options.findIndex((option) => !option.disabled);
  }

  private positionPanel(): void {
    if (typeof window === 'undefined') return;

    const triggerRect = this.trigger().nativeElement.getBoundingClientRect();
    const panel = this.panel().nativeElement;
    const viewportPadding = 8;
    const gap = 6;
    const maxPanelWidth = Math.max(0, window.innerWidth - viewportPadding * 2);

    panel.style.width = 'max-content';
    panel.style.maxWidth = `${maxPanelWidth}px`;
    panel.style.maxHeight = '320px';

    const naturalWidth = panel.getBoundingClientRect().width;
    const panelWidth = Math.min(Math.max(triggerRect.width, naturalWidth), maxPanelWidth);
    panel.style.width = `${panelWidth}px`;

    const spaceBelow = Math.max(0, window.innerHeight - triggerRect.bottom - viewportPadding);
    const spaceAbove = Math.max(0, triggerRect.top - viewportPadding);
    const desiredHeight = Math.min(panel.scrollHeight, 320);
    const openAbove = desiredHeight > spaceBelow && spaceAbove > spaceBelow;
    const availableHeight = openAbove ? spaceAbove : spaceBelow;
    const maxHeight = Math.max(64, Math.min(320, availableHeight - gap));
    panel.style.maxHeight = `${maxHeight}px`;

    const panelHeight = Math.min(panel.scrollHeight, maxHeight);
    const desiredLeft =
      this.align() === 'end' ? triggerRect.right - panelWidth : triggerRect.left;
    const left = Math.min(
      Math.max(viewportPadding, desiredLeft),
      Math.max(viewportPadding, window.innerWidth - viewportPadding - panelWidth),
    );
    const top = openAbove
      ? Math.max(viewportPadding, triggerRect.top - gap - panelHeight)
      : Math.min(
          triggerRect.bottom + gap,
          Math.max(viewportPadding, window.innerHeight - viewportPadding - panelHeight),
        );

    panel.style.left = `${Math.round(left)}px`;
    panel.style.top = `${Math.round(top)}px`;
  }
}
