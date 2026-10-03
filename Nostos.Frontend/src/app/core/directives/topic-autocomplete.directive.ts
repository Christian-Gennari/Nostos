import { Directive, ElementRef, EventEmitter, HostListener, Output } from '@angular/core';
import { TopicAutocompleteService } from '../../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { TopicDto } from '../services/topics.service';

@Directive({
  selector: '[noteAutocomplete]',
  standalone: true,
})
export class TopicAutocompleteDirective {
  @Output() insertTopic = new EventEmitter<TopicDto>();
  @Output() insertTopicName = new EventEmitter<string>();

  constructor(
    private el: ElementRef<HTMLTextAreaElement>,
    private auto: TopicAutocompleteService,
  ) {}

  @HostListener('input')
  onInput(): void {
    const textarea = this.el.nativeElement;
    this.auto.update(textarea.value, textarea.selectionStart);
  }

  @HostListener('keydown', ['$event'])
  onKeyDown(event: KeyboardEvent): void {
    if (!this.auto.pickerOpen()) return;

    if (event.key === 'Escape') {
      event.preventDefault();
      this.auto.clear();
      return;
    }

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

    const chosen = this.auto.choose();
    const query = this.auto.query().trim();
    if (!chosen && !query) return;

    event.preventDefault();
    if (chosen) this.insertTopic.emit(chosen);
    else this.insertTopicName.emit(query);
    this.auto.clear();
  }
}
