import { Directive, ElementRef, EventEmitter, HostListener, Output } from '@angular/core';
import { TopicAutocompleteService } from '../../ui/topic-autocomplete-panel/topic-autocomplete.service';

@Directive({
  selector: '[noteAutocomplete]',
  standalone: true,
})
export class TopicAutocompleteDirective {
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
      // Stopped here so Escape closes the suggestions and nothing around them.
      event.preventDefault();
      event.stopPropagation();
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

    const name = this.auto.activeName();
    if (!name) return;

    event.preventDefault();
    this.insertTopicName.emit(name);
    this.auto.clear();
  }

  /** Leaving the textarea ends an inline suggestion session; the dialog owns its own focus. */
  @HostListener('blur')
  onBlur(): void {
    if (this.auto.pickerOpen() && this.auto.mode() === 'inline') this.auto.clear();
  }
}
