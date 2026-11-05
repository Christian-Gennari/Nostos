import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  EventEmitter,
  OnDestroy,
  Output,
  TemplateRef,
  ViewContainerRef,
  effect,
  inject,
  input,
  untracked,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { Overlay, OverlayConfig, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';

import { NO_TOPICS_YET, TopicOptionComponent } from '../topic-option/topic-option.component';
import { TopicAutocompleteService } from './topic-autocomplete.service';

/**
 * The `[[` suggestions behind a note's words: a list anchored to the textarea,
 * above it when there is room and below it when there is not. It renders into
 * an overlay attached to the document, so a scrolling or clipping ancestor (the
 * reader panel, a card near the top of a list) cannot cut it off. A topic that
 * is a label is added with the composer's **+ Topic** pill instead.
 */
@Component({
  standalone: true,
  selector: 'topic-autocomplete-panel',
  imports: [NgTemplateOutlet, TopicOptionComponent],
  templateUrl: './topic-autocomplete-panel.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./topic-autocomplete-panel.css'],
})
export class TopicAutocompletePanel implements OnDestroy {
  readonly auto = inject(TopicAutocompleteService);
  readonly noTopics = NO_TOPICS_YET;
  private readonly overlay = inject(Overlay);
  private readonly viewContainer = inject(ViewContainerRef);

  /** The textarea the inline suggestion list hangs from. */
  readonly anchor = input<HTMLElement | null>(null);

  @Output() topicNameSelected = new EventEmitter<string>();
  @Output() cancelled = new EventEmitter<void>();

  private readonly inlineTpl = viewChild.required<TemplateRef<unknown>>('inlineTpl');

  private overlayRef: OverlayRef | null = null;
  private shown = false;

  constructor() {
    effect(() => {
      const open = this.auto.pickerOpen();
      untracked(() => this.render(open));
    });

    effect(() => {
      if (this.auto.keyboardMoves() === 0) return;
      const id = untracked(() => this.auto.activeOptionId());
      if (!id) return;
      document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  ngOnDestroy(): void {
    this.render(false);
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

  /** Tears the overlay down now, before the input places the caret. */
  private close(): void {
    this.auto.clear();
    this.render(false);
  }

  private render(open: boolean): void {
    if (open === this.shown) return;

    this.overlayRef?.dispose();
    this.overlayRef = null;
    this.shown = open;
    const anchor = this.anchor();
    if (!open || !anchor) return;

    this.overlayRef = this.overlay.create(this.inlineConfig(anchor));
    this.overlayRef.attach(new TemplatePortal(this.inlineTpl(), this.viewContainer));
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
