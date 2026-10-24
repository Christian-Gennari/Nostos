import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  EventEmitter,
  Injector,
  OnDestroy,
  Output,
  TemplateRef,
  ViewContainerRef,
  afterNextRender,
  effect,
  inject,
  input,
  untracked,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { Overlay, OverlayConfig, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';

import { InputDirective } from '../form-control/form-control.directive';
import { SearchFieldComponent } from '../search-field/search-field.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { TopicAutocompleteService, TopicPickerMode } from './topic-autocomplete.service';

/**
 * The topic picker behind a note input.
 *
 * Both presentations render into an overlay attached to the document rather
 * than beside the textarea. The picker used to be an absolutely positioned box
 * pinned above its input, so any scrolling or clipping ancestor (the reader
 * panel, the note inspector, a card near the top of a list) could cut it off.
 *
 * - `dialog` — opened by the "Link a topic" button: the shared modal shell,
 *   which is a full-screen sheet on a phone.
 * - `inline` — opened by typing `[[`: a suggestion list anchored to the
 *   textarea, above it when there is room and below it when there is not.
 */
@Component({
  standalone: true,
  selector: 'topic-autocomplete-panel',
  imports: [NgTemplateOutlet, ModalShell, InputDirective, IconButtonComponent, NostosIconComponent, SearchFieldComponent],
  templateUrl: './topic-autocomplete-panel.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./topic-autocomplete-panel.css'],
})
export class TopicAutocompletePanel implements OnDestroy {
  readonly auto = inject(TopicAutocompleteService);
  private readonly overlay = inject(Overlay);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly injector = inject(Injector);

  /** The textarea the inline suggestion list hangs from. */
  readonly anchor = input<HTMLElement | null>(null);

  @Output() topicNameSelected = new EventEmitter<string>();
  @Output() cancelled = new EventEmitter<void>();

  private readonly dialogTpl = viewChild.required<TemplateRef<unknown>>('dialogTpl');
  private readonly inlineTpl = viewChild.required<TemplateRef<unknown>>('inlineTpl');

  private overlayRef: OverlayRef | null = null;
  private shown: TopicPickerMode | null = null;

  /**
   * Capture phase, so Escape closes only this dialog. Left to bubble it would
   * also reach whatever the note input sits in (a reader panel, another dialog)
   * and close that too.
   */
  private readonly onDialogKeydown = (event: KeyboardEvent): void => {
    if (event.key !== 'Escape') return;
    event.preventDefault();
    event.stopPropagation();
    this.cancel();
  };

  constructor() {
    effect(() => {
      const mode = this.auto.pickerOpen() ? this.auto.mode() : null;
      untracked(() => this.render(mode));
    });

    effect(() => {
      if (this.auto.keyboardMoves() === 0) return;
      const id = untracked(() => this.auto.activeOptionId());
      if (!id) return;
      document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  ngOnDestroy(): void {
    this.render(null);
  }

  onSearchInput(event: Event): void {
    this.auto.setQuery((event.target as HTMLInputElement).value);
  }

  onSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.auto.moveDown();
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.auto.moveUp();
      return;
    }

    if (event.key !== 'Enter') return;

    const name = this.auto.activeName();
    if (!name) return;
    event.preventDefault();
    this.select(name);
  }

  select(name: string, event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.close();
    this.topicNameSelected.emit(name);
  }

  cancel(event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.close();
    this.cancelled.emit();
  }

  /**
   * Tears the overlay down now rather than on the next change-detection pass:
   * the dialog's focus trap hands focus back as it is destroyed, and the note
   * input then places the caret, so the order has to be fixed.
   */
  private close(): void {
    this.auto.clear();
    this.render(null);
  }

  private render(mode: TopicPickerMode | null): void {
    if (mode === this.shown) return;

    document.removeEventListener('keydown', this.onDialogKeydown, true);
    this.overlayRef?.dispose();
    this.overlayRef = null;
    this.shown = mode;
    if (!mode) return;

    const anchor = this.anchor();
    if (mode === 'inline' && !anchor) return;

    this.overlayRef = this.overlay.create(
      mode === 'inline' ? this.inlineConfig(anchor!) : new OverlayConfig()
    );
    this.overlayRef.attach(
      new TemplatePortal(mode === 'inline' ? this.inlineTpl() : this.dialogTpl(), this.viewContainer)
    );

    if (mode !== 'dialog') return;

    document.addEventListener('keydown', this.onDialogKeydown, true);
    const pane = this.overlayRef.overlayElement;
    afterNextRender(
      () => {
        const search = pane.querySelector<HTMLInputElement>('.picker-search-input');
        search?.focus();
        search?.select();
      },
      { injector: this.injector }
    );
  }

  private inlineConfig(anchor: HTMLElement): OverlayConfig {
    const width = Math.min(Math.max(anchor.getBoundingClientRect().width, 240), 420);

    return new OverlayConfig({
      width,
      maxWidth: 'calc(100vw - 16px)',
      scrollStrategy: this.overlay.scrollStrategies.reposition(),
      positionStrategy: this.overlay
        .position()
        .flexibleConnectedTo(anchor)
        .withPositions([
          { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -6 },
          { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
        ])
        .withFlexibleDimensions(false)
        .withPush(true)
        .withViewportMargin(8),
    });
  }
}
