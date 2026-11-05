import {
  Component,
  ElementRef,
  EventEmitter,
  HostBinding,
  Input,
  OnInit,
  Output,
  ViewChild,
  ChangeDetectorRef,
  forwardRef,
  inject,
  ChangeDetectionStrategy,
  effect,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR, FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';

import { TopicAutocompleteDirective } from '../../core/directives/topic-autocomplete.directive';
import { TopicAutocompletePanel } from '../topic-autocomplete-panel/topic-autocomplete-panel.component';
import { BrainStateService } from '../../brain/brain-state.service';
import { TopicAutocompleteService } from '../topic-autocomplete-panel/topic-autocomplete.service';

/**
 * The words of a note, with `[[` autocomplete for a topic inside a sentence.
 * Used only inside the note composer (`app-note-composer`), which adds the
 * topic pills below it.
 */
@Component({
  selector: 'app-topic-input',
  standalone: true,
  imports: [CommonModule, FormsModule, TopicAutocompleteDirective, TopicAutocompletePanel],
  templateUrl: './topic-input.component.html',
  styleUrls: ['./topic-input.component.css'],
  changeDetection: ChangeDetectionStrategy.Eager,
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => TopicInputComponent),
      multi: true,
    },
    TopicAutocompleteService,
  ],
})
export class TopicInputComponent implements ControlValueAccessor, OnInit {
  @Input() placeholder = '';
  @Input() rows = 3;
  @Input() inputId: string | null = null;
  @Input() ariaLabel: string | null = null;
  @Input() testId: string | null = null;
  /** The reader's selection bar and highlight card: a tighter field. */
  @Input() @HostBinding('class.is-compact') compact = false;
  @Output() submitTrigger = new EventEmitter<void>();

  @ViewChild('textarea') textarea!: ElementRef<HTMLTextAreaElement>;
  protected readonly autocompleteService = inject(TopicAutocompleteService);
  private readonly brain = inject(BrainStateService);
  private readonly cdr = inject(ChangeDetectorRef);

  value = '';
  isDisabled = false;

  onChange = (_value: string) => {};
  onTouched = () => {};

  /** True while the `[[` suggestion list is showing, for the textarea's ARIA wiring. */
  protected inlineOpen(): boolean {
    return this.autocompleteService.pickerOpen();
  }

  constructor() {
    // The suggestions are the Brain's topics, which the page already holds or
    // is loading: asking the server again for each field would repeat that call.
    effect(() => this.autocompleteService.setTopics(this.brain.topics()));
  }

  ngOnInit(): void {
    this.brain.ensureTopicsLoaded();
  }

  focus(): void {
    this.textarea?.nativeElement.focus();
  }

  /** The `[[` list closed without a choice: back to the words. */
  cancelTopicPicker(): void {
    setTimeout(() => this.textarea?.nativeElement.focus(), 0);
  }

  insertTopicName(rawName: string): void {
    const name = rawName.trim();
    if (!name) return;

    const textarea = this.textarea.nativeElement;
    const text = this.value;
    const cursor = textarea.selectionStart ?? text.length;
    const wikilinkStart = text.lastIndexOf('[[', cursor);
    const lastClose = text.lastIndexOf(']]', Math.max(0, cursor - 1));
    const inLink = wikilinkStart >= 0 && lastClose < wikilinkStart;
    const start = inLink ? wikilinkStart : cursor;
    const end = cursor;

    const link = `[[${name}]]`;
    const after = text.slice(end);
    const spacer = after.length === 0 || (!/^\s/.test(after) && !/^[,.;:!?)]/.test(after))
      ? ' '
      : '';

    this.value = text.slice(0, start) + link + spacer + after;
    // Written to the field now, not at the next render: a key typed straight
    // after Enter must land after the link, not overwrite it.
    textarea.value = this.value;
    const caret = start + link.length + spacer.length;
    textarea.setSelectionRange(caret, caret);
    this.onChange(this.value);
    this.autocompleteService.clear();
    if (document.activeElement !== textarea) textarea.focus();
  }

  writeValue(obj: unknown): void {
    this.value = typeof obj === 'string' ? obj : '';
    this.cdr.markForCheck();
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.isDisabled = isDisabled;
  }

  handleInput(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;
    this.value = value;
    this.onChange(value);
  }
}
