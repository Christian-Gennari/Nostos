import {
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnDestroy,
  OnInit,
  Output,
  ViewChild,
  ChangeDetectorRef,
  forwardRef,
  inject,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR, FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';

import { TopicAutocompleteDirective } from '../../core/directives/topic-autocomplete.directive';
import { TopicAutocompletePanel } from '../topic-autocomplete-panel/topic-autocomplete-panel.component';
import { TopicDto, TopicsService } from '../../core/services/topics.service';
import { TopicAutocompleteService } from '../topic-autocomplete-panel/topic-autocomplete.service';

@Component({
  selector: 'app-topic-input',
  standalone: true,
  imports: [CommonModule, FormsModule, TopicAutocompleteDirective, TopicAutocompletePanel],
  templateUrl: './topic-input.component.html',
  styleUrls: ['./topic-input.component.css'],
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => TopicInputComponent),
      multi: true,
    },
    TopicAutocompleteService,
  ],
})
export class TopicInputComponent implements ControlValueAccessor, OnInit, OnDestroy {
  @Input() placeholder = '';
  @Input() rows = 3;
  @Output() submitTrigger = new EventEmitter<void>();

  @ViewChild('textarea') textarea!: ElementRef<HTMLTextAreaElement>;
  @ViewChild(TopicAutocompletePanel) private topicPanel!: TopicAutocompletePanel;

  private readonly autocompleteService = inject(TopicAutocompleteService);
  private readonly topicsService = inject(TopicsService);
  private readonly cdr = inject(ChangeDetectorRef);
  private sub?: Subscription;
  private pickerSelection: { start: number; end: number } | null = null;

  value = '';
  isDisabled = false;

  onChange = (_value: string) => {};
  onTouched = () => {};

  ngOnInit(): void {
    this.sub = this.topicsService.list().subscribe((list) => {
      this.autocompleteService.setTopics(list);
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  rememberSelection(): void {
    const textarea = this.textarea?.nativeElement;
    if (!textarea) return;
    this.pickerSelection = {
      start: textarea.selectionStart ?? this.value.length,
      end: textarea.selectionEnd ?? this.value.length,
    };
  }

  openTopicPicker(): void {
    this.rememberSelection();
    const selection = this.pickerSelection;
    const prefill = selection
      ? this.value.slice(selection.start, selection.end).trim()
      : '';

    this.autocompleteService.openPicker(prefill);
    this.topicPanel.focusSearch();
  }

  cancelTopicPicker(): void {
    const selection = this.pickerSelection;
    this.autocompleteService.clear();
    this.pickerSelection = null;

    setTimeout(() => {
      const textarea = this.textarea.nativeElement;
      textarea.focus();
      if (selection) textarea.setSelectionRange(selection.start, selection.end);
    }, 0);
  }

  insertTopic(topic: TopicDto): void {
    this.insertTopicName(topic.name);
  }

  insertTopicName(rawName: string): void {
    const name = rawName.trim();
    if (!name) return;

    const textarea = this.textarea.nativeElement;
    const text = this.value;
    const storedSelection = this.pickerSelection;

    let start: number;
    let end: number;

    if (storedSelection) {
      start = storedSelection.start;
      end = storedSelection.end;
    } else {
      const cursor = textarea.selectionStart ?? text.length;
      const wikilinkStart = text.lastIndexOf('[[', cursor);
      const lastClose = text.lastIndexOf(']]', Math.max(0, cursor - 1));

      if (wikilinkStart >= 0 && lastClose < wikilinkStart) {
        start = wikilinkStart;
        end = cursor;
      } else {
        start = cursor;
        end = cursor;
      }
    }

    const link = `[[${name}]]`;
    const after = text.slice(end);
    const spacer = after.length === 0 || (!/^\s/.test(after) && !/^[,.;:!?)]/.test(after))
      ? ' '
      : '';

    this.value = text.slice(0, start) + link + spacer + after;
    this.onChange(this.value);
    this.autocompleteService.clear();
    this.pickerSelection = null;

    const caret = start + link.length + spacer.length;
    setTimeout(() => {
      textarea.focus();
      textarea.setSelectionRange(caret, caret);
    }, 0);
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
    this.pickerSelection = null;
    this.onChange(value);
  }
}
