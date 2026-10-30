/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { NOSTOS_MENU } from '../menu/menu.component';
import { ChangeDetectionStrategy, Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { THEMES, ThemeName, ThemeService } from '../../core/services/theme.service';
import { ToastService, type ToastType } from '../../core/services/toast.service';
import { BadgeComponent } from '../badge/badge.component';
import { BrandLockupComponent } from '../brand-lockup/brand-lockup.component';
import { NoticeComponent } from '../notice/notice.component';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { ButtonComponent } from '../button/button.component';
import { ChipComponent } from '../chip/chip.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { InputDirective, TextareaDirective } from '../form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../dropdown/dropdown.component';
import { FormFieldComponent } from '../form-field/form-field.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { SegmentedComponent } from '../segmented/segmented.component';
import { SearchFieldComponent } from '../search-field/search-field.component';
import { InlineRenameComponent } from '../inline-rename/inline-rename.component';
import { ChoiceRowComponent } from '../choice-row/choice-row.component';
import { ConfirmModal } from '../confirm-modal/confirm-modal.component';
import { DropZoneComponent } from '../drop-zone/drop-zone.component';
import { FlatTreeComponent } from '../flat-tree/flat-tree.component';
import type { TreeItem } from '../flat-tree/flat-tree.helper';
import { EditionSwitcherComponent, type EditionSwitcherOption } from '../edition-switcher/edition-switcher.component';
import { StarRatingComponent } from '../star-rating/star-rating.component';
import { CollectionPickerComponent } from '../collection-picker/collection-picker.component';
import { RangeDirective } from '../range/range.directive';
import { TopicPickerPopoverComponent } from '../topic-picker-popover/topic-picker-popover.component';
import { NoteItemComponent } from '../note-item/note-item.component';
import { NoteCaptureDetailsComponent } from '../note-capture-details/note-capture-details.component';
import type { NoteSearchHit } from '../../core/dtos/note.dtos';
import type { TopicDto } from '../../core/services/topics.service';
import type { Collection } from '../../core/dtos/collection.dtos';
import { CheckboxDirective, RadioDirective } from '../check/check.directive';
import { ProgressComponent } from '../progress/progress.component';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { SwitchComponent } from '../switch/switch.component';
import { ViewToggleComponent, type ViewToggleOption } from '../view-toggle/view-toggle.component';
import { BookCoverComponent } from '../book-cover/book-cover.component';
import { PenSwatchesComponent } from '../pen-swatches/pen-swatches.component';
import { QuoteBlockComponent } from '../quote-block/quote-block.component';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { PagingStatusComponent } from '../paging-status/paging-status.component';
import { TopicChipComponent } from '../topic-chip/topic-chip.component';
import { NO_TOPICS_YET, TopicOptionComponent } from '../topic-option/topic-option.component';
import type { HighlightColour } from '../../reader/highlight-colours';
import { PlaybackSpeedMenuComponent } from '../playback-speed-menu/playback-speed-menu.component';
import { SettingsPopoverComponent } from '../settings-popover/settings-popover.component';
import { TO_READ_LABEL } from '../../core/reading-status';

/**
 * Inspectable reference fixture for Nostos UI v1.
 *
 * This is deliberately a lightweight application route rather than Storybook:
 * every example runs inside the same Angular app, token graph and theme service
 * as production surfaces, and Playwright exercises it at the app's desktop and
 * mobile widths.
 */
@Component({
  selector: 'app-ui-catalogue',
  standalone: true,
  imports: [
    BookCoverComponent,
    BadgeComponent,
    BrandLockupComponent,
    NoticeComponent,
    IconTileComponent,
    NostosIconComponent,
    ButtonComponent,
    ChipComponent,
    DialogActionsComponent,
    FormFieldComponent,
    IconButtonComponent,
    SegmentedComponent,
    SearchFieldComponent,
    InlineRenameComponent,
    CheckboxDirective,
    RadioDirective,
    ProgressComponent,
    InputDirective,
    ModalShell,
    LoadingIndicatorComponent,
    DropdownComponent,
    ...NOSTOS_MENU,
    PlaybackSpeedMenuComponent,
    SettingsPopoverComponent,
    SwitchComponent,
    ViewToggleComponent,
    TextareaDirective,
    PenSwatchesComponent,
    QuoteBlockComponent,
    TopicChipComponent,
    TopicOptionComponent,
    EmptyStateComponent,
    PagingStatusComponent,
    ChoiceRowComponent,
    ConfirmModal,
    DropZoneComponent,
    FlatTreeComponent,
    EditionSwitcherComponent,
    StarRatingComponent,
    CollectionPickerComponent,
    RangeDirective,
    TopicPickerPopoverComponent,
    NoteItemComponent,
    NoteCaptureDetailsComponent,
  ],
  templateUrl: './ui-catalogue.component.html',
  styleUrl: './ui-catalogue.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UiCatalogueComponent {
  protected readonly toReadLabel = TO_READ_LABEL;
  private readonly themeService = inject(ThemeService);
  private readonly toastService = inject(ToastService);

  /** Every theme, so each control can be checked in all five. */
  readonly themes = THEMES;
  readonly theme = this.themeService.active;
  readonly selectedTab = signal<'primitives' | 'patterns'>('primitives');
  /** Deliberately a plain string: the component is not generic, so the demo does not
      pretend to narrow the value it receives. */
  readonly selectedSegment = signal(1.6);
  readonly segmentedOptions = [1.4, 1.6, 1.8, 2.0].map((value) => ({ value, label: value.toFixed(1) }));
  readonly selectedView = signal('list');
  readonly viewToggleOptions = [
    { value: 'list', icon: 'list-bullets', label: 'List view' },
    { value: 'grid', icon: 'squares-four', label: 'Grid view' },
  ] satisfies readonly ViewToggleOption[];
  readonly modalOpen = signal(false);
  readonly confirmOpen = signal(false);
  readonly catalogueTree: TreeItem[] = [
    { id: 'catalogue-tree-philosophy', name: 'Philosophy', parentId: null, count: 4 },
    { id: 'catalogue-tree-ethics', name: 'Ethics', parentId: 'catalogue-tree-philosophy', count: 2 },
    { id: 'catalogue-tree-poetry', name: 'Poetry', parentId: null, count: 7 },
  ];
  readonly catalogueEditions: EditionSwitcherOption[] = [
    { id: 'catalogue-epub', format: 'Ebook', concept: 'ebook', detail: null, current: true, progress: 37, finished: false, description: 'Ebook, 37% read' },
    { id: 'catalogue-audio', format: 'Audiobook', concept: 'audiobook', detail: 'Read by a guest narrator', current: false, progress: 34, finished: false, rating: 2, ratingNoun: 'narration', description: 'Audiobook, read by a guest narrator, 34% read, narration rated 2 out of 5' },
  ];
  readonly catalogueEditionId = signal<string | null>(null);
  readonly catalogueCollections: Collection[] = [
    { id: 'catalogue-philosophy', name: 'Philosophy', parentId: null },
    { id: 'catalogue-ethics', name: 'Ethics', parentId: 'catalogue-philosophy' },
    { id: 'catalogue-poetry', name: 'Poetry', parentId: null },
  ];
  readonly catalogueCollectionIds = signal<string[]>(['catalogue-ethics']);
  readonly catalogueRating = signal(4);
  readonly catalogueVolume = signal(60);
  readonly catalogueTopicPickerOpen = signal(false);
  readonly catalogueTopicChoices = [
    { id: 't1', name: 'Virtue', usageCount: 12 },
    { id: 't2', name: 'Habit', usageCount: 3 },
    { id: 't3', name: 'Prudence', usageCount: 5 },
  ] as unknown as readonly TopicDto[];
  readonly catalogueNote: NoteSearchHit = {
    id: 'catalogue-note',
    bookId: 'catalogue-book',
    bookTitle: 'On the good life',
    bookAuthor: 'Aristotle',
    content: 'Virtue is a settled disposition, not a single act.',
    selectedText: 'Virtue, then, is a state of character concerned with choice.',
    snippet: null,
    topicNames: ['Virtue', 'Habit'],
    createdAt: '2026-10-01T09:00:00Z',
    highlightColour: 'amber',
    hasRawContent: true,
    processingMode: 'light_polish',
    captureSource: 'typed',
  };
  readonly renameDemo = signal(true);
  readonly cataloguePen = signal<HighlightColour>('amber');
  readonly catalogueTopics = signal(['Virtue', 'Habit', 'The good life']);
  readonly noTopicsYet = NO_TOPICS_YET;
  readonly catalogueSettingsOpen = signal(false);
  readonly catalogueSettingsButton = viewChild('settingsTrigger', { read: ElementRef<HTMLElement> });
  readonly catalogueChoices = ['Recent', 'Title', 'Author'];
  readonly catalogueChoice = signal('Recent');
  readonly catalogueRate = signal(1);
  readonly catalogueReadAloudRate = signal(1);
  readonly catalogueDropdownValue = signal('epub');
  readonly dropdownOptions = [
    { value: 'epub', label: 'EPUB' },
    { value: 'pdf', label: 'PDF' },
    { value: 'physical', label: 'Physical' },
    { value: 'unavailable', label: 'Unavailable format', disabled: true },
  ] satisfies readonly DropdownOption[];
  readonly longDropdownOptions = Array.from({ length: 18 }, (_, index) => ({
    value: `chapter-${index + 1}`,
    label: `Chapter ${index + 1}`,
  })) satisfies readonly DropdownOption[];

  showCatalogueToast(type: ToastType): void {
    const messages: Record<ToastType, string> = {
      success: 'Book details saved',
      error: 'Could not save the book details',
      info: 'Metadata refresh started',
    };
    this.toastService.show(messages[type], type, 30_000);
  }

  showLongCatalogueToast(): void {
    this.toastService.show(
      'A long notification wraps cleanly without pushing the dismiss control outside the stable toast width.',
      'info',
      30_000,
    );
  }

  showStackedCatalogueToasts(): void {
    this.toastService.show('Book details saved', 'success', 30_000);
    this.toastService.show('Metadata refresh started', 'info', 30_000);
    this.toastService.show('Could not save the book details', 'error', 30_000);
  }

  setTheme(theme: ThemeName): void {
    this.themeService.setChoice(theme);
  }
}
