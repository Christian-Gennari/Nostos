import { Component, Input, Output, EventEmitter, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NostosIconComponent } from '../icon/nostos-icon.component';

@Component({
  selector: 'app-star-rating',
  standalone: true,
  imports: [CommonModule, NostosIconComponent],
  template: `
    <div class="star-rating" [class.readonly]="readonly">
      @for (star of stars; track $index) {
      <nostos-icon
        name="star"
        [size]="size"
        class="star-icon"
        [class.filled]="$index < rating"
        [weight]="$index < rating ? 'fill' : 'regular'"
        (click)="rate($index + 1)"></nostos-icon>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .star-rating {
        display: flex;
        align-items: center;
        gap: 2px;
      }
      .star-icon {
        color: var(--color-text-placeholder);
        /* Named properties, not "all". What actually changes between states:
           transform (the hover scale) and color/fill (the filled state).
           transform stays in the list deliberately — the 1.1x hover scale is the
           intended affordance, not an accidental layout animation. */
        transition: transform 0.2s ease, color 0.2s ease;
      }
      /* Hover effects only if not readonly */
      .star-rating:not(.readonly) .star-icon:hover {
        transform: scale(1.1);
        cursor: pointer;
      }
      .star-rating:not(.readonly):hover .star-icon {
        /* Optional: highlight all stars up to hover could go here,
         but simple fill is often enough */
      }

      /* The filled state is a WEIGHT now, not a paint trick. This rule used to
         ask for a fill on the icon host, which the old stroke-drawn library could
         not honour — its svg carried fill="none", and an inherited value cannot
         override an element's own attribute, so the declaration was inert and a
         filled star was only ever a recoloured outline. The component's template
         now switches the glyph to the fill weight, which is what this rule was
         describing all along; the color below is what the solid glyph is
         painted with, and quotes are used here so a backtick cannot close the
         inline styles literal early (check:design guards this). */
      .star-icon.filled {
        color: var(--color-highlight);
      }
    `,
  ],
})
export class StarRatingComponent {
  @Input() rating = 0;
  @Input() readonly = false;
  @Input() size = 18;
  @Output() ratingChange = new EventEmitter<number>();
  stars = new Array(5); // Dummy array for loop

  rate(val: number) {
    if (this.readonly) return;

    // Logic change: If the clicked star value is the current rating, set it to 0 (reset). Otherwise, set it to the new value.
    const newRating = this.rating === val ? 0 : val;
    this.ratingChange.emit(newRating);
  }
}
