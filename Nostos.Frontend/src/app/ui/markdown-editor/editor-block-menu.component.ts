import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

import { BlockMenuAction, BlockMenuItem } from './block-menu';

export interface BlockMenuRect {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export interface BlockMenuOverlayState {
  items: BlockMenuItem[];
  rect: BlockMenuRect | null;
}

export interface BlockMenuPlacement {
  top: number | null;
  bottom: number | null;
  left: number;
  maxHeight: number;
}

let blockImageInputs = 0;

/** Computes where the `/` menu sits: under the line, or above it when the page has no room below. */
export function computeBlockMenuPlacement(rect: BlockMenuRect | null | undefined): BlockMenuPlacement | null {
  if (!rect) return null;
  // The visual viewport is what is on screen: with the soft keyboard up it is
  // shorter than the window, and the menu must fit above the keyboard.
  const vv = typeof window === 'undefined' ? null : window.visualViewport;
  const layoutHeight = typeof window === 'undefined' ? 800 : window.innerHeight;
  const layoutWidth = typeof window === 'undefined' ? 1280 : window.innerWidth;
  const viewTop = vv?.offsetTop ?? 0;
  const viewBottom = vv ? vv.offsetTop + vv.height : layoutHeight;
  const viewLeft = vv?.offsetLeft ?? 0;
  const viewWidth = vv?.width ?? layoutWidth;
  const below = viewBottom - rect.bottom - 12;
  const above = rect.top - viewTop - 12;
  const cap = Math.min(360, (viewBottom - viewTop) * 0.55);
  // The menu is `min(300px, 100vw - 24px)` wide (see the stylesheet): keep all of it on screen.
  const width = Math.min(300, viewWidth - 24);
  const left = Math.max(viewLeft + 12, Math.min(rect.left, viewLeft + viewWidth - width - 12));
  if (below < Math.min(cap, 240) && above > below) {
    return { top: null, bottom: layoutHeight - rect.top + 6, left, maxHeight: Math.min(cap, above) };
  }
  return { top: rect.bottom + 6, bottom: null, left, maxHeight: Math.min(cap, below) };
}

/**
 * The `/` block menu popover and its second-step image URL prompt (issue #1043).
 */
@Component({
  selector: 'app-editor-block-menu',
  standalone: true,
  templateUrl: './editor-block-menu.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    style: 'display: contents',
  },
})
export class EditorBlockMenuComponent {
  readonly menu = input<BlockMenuOverlayState | null>(null);
  readonly activeIndex = input<number>(0);
  readonly imageMode = input<boolean>(false);
  readonly imageUrl = input<string>('');

  readonly activeIndexChange = output<number>();
  readonly actionSelect = output<BlockMenuAction>();
  readonly imageUrlChange = output<string>();
  readonly imageSubmit = output<void>();
  readonly imageCancel = output<void>();

  /** Unique per editor, so the address field's label finds its own input. */
  readonly blockImageInputId = `block-image-url-${++blockImageInputs}`;
  readonly placement = computed(() => computeBlockMenuPlacement(this.menu()?.rect));

  private readonly blockImageInput = viewChild<ElementRef<HTMLInputElement>>('blockImageInput');
  private readonly destroyRef = inject(DestroyRef);
  private focusTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect((onCleanup) => {
      const open = !!this.menu() && this.imageMode();
      const inputEl = this.blockImageInput()?.nativeElement;
      this.clearFocusTimer();
      if (!open || !inputEl) return;
      this.focusTimer = setTimeout(() => {
        this.focusTimer = null;
        inputEl.focus();
      });
      onCleanup(() => this.clearFocusTimer());
    });

    this.destroyRef.onDestroy(() => this.clearFocusTimer());
  }

  /** Cancels any pending focus timer when the image prompt or menu closes. */
  clearFocusTimer(): void {
    if (this.focusTimer !== null) {
      clearTimeout(this.focusTimer);
      this.focusTimer = null;
    }
  }
}
