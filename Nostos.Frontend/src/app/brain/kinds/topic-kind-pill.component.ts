import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { kindCssVar } from '../topic-map/topic-map.helpers';

/**
 * A topic kind as a coloured capsule. It is presentational: the caller decides
 * whether it sits in a button (the map card, the picker) or plain text (a row
 * in the sorter). The dot carries the palette colour, the label the name.
 */
@Component({
  selector: 'app-topic-kind-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      class="kind-pill"
      [class.is-untyped]="!name()"
      [style.--kind-color]="color()"
    >
      <span class="kind-dot" aria-hidden="true"></span>
      <span class="kind-label">{{ name() ?? untypedLabel() }}</span>
    </span>
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        min-width: 0;
      }

      .kind-pill {
        display: inline-flex;
        align-items: center;
        gap: 0.4rem;
        min-width: 0;
        padding: 3px 9px 3px 8px;
        border: 1px solid color-mix(in srgb, var(--kind-color) 40%, var(--border-color));
        border-radius: var(--radius-pill);
        background: color-mix(in srgb, var(--kind-color) 13%, transparent);
        color: var(--color-text-main);
        font-size: 0.78rem;
        font-weight: var(--fw-medium);
        line-height: 1.25;
        white-space: nowrap;
      }

      .kind-pill.is-untyped {
        border-color: var(--border-color);
        background: var(--bg-hover);
        color: var(--color-text-muted);
      }

      .kind-dot {
        flex: 0 0 auto;
        width: 8px;
        height: 8px;
        border-radius: var(--radius-round);
        background: var(--kind-color);
      }

      .kind-label {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
      }
    `,
  ],
})
export class TopicKindPillComponent {
  readonly name = input<string | null>(null);
  readonly colorKey = input<string | null>(null);
  readonly untypedLabel = input('No kind');

  readonly color = computed(() => kindCssVar(this.colorKey()));
}
