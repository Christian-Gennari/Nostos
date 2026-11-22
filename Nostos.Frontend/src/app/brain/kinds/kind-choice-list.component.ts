import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import type { TopicKindDto } from '../../core/services/topics.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { kindCssVar } from '../topic-map/topic-map.helpers';

/**
 * The list of kinds a topic may take, used by the picker and by the sorter's
 * bulk "Set kind…" panel. Each row is a colour dot, the name, and a tick on the
 * current choice; "No kind" is offered first so clearing is never hidden.
 */
@Component({
  selector: 'app-kind-choice-list',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="kind-choices" [attr.aria-label]="label()">
      @if (includeNone()) {
        <li>
          <button appButton variant="ghost" type="button" class="kind-choice"
            [class.is-current]="selectedId() === null" (click)="picked.emit(null)">
            <span class="kind-dot is-untyped" aria-hidden="true"></span>
            <span class="kind-choice-label">No kind</span>
            @if (selectedId() === null) {
              <nostos-icon class="kind-check" name="check" [size]="14" />
            }
          </button>
        </li>
      }
      @for (kind of kinds(); track kind.id) {
        <li>
          <button appButton variant="ghost" type="button" class="kind-choice"
            [class.is-current]="selectedId() === kind.id" (click)="picked.emit(kind.id)">
            <span class="kind-dot" [style.--kind-color]="colorOf(kind)" aria-hidden="true"></span>
            <span class="kind-choice-label">{{ kind.name }}</span>
            @if (selectedId() === kind.id) {
              <nostos-icon class="kind-check" name="check" [size]="14" />
            }
          </button>
        </li>
      }
    </ul>
  `,
  styles: [
    `
      :host {
        display: block;
      }

      .kind-choices {
        display: flex;
        flex-direction: column;
        gap: 1px;
        margin: 0;
        padding: 0;
        list-style: none;
      }

      .kind-choice {
        justify-content: flex-start;
        gap: 0.55rem;
        width: 100%;
        min-height: 34px;
        padding: 0.35rem 0.6rem;
        border-radius: var(--radius-popover-item);
        color: var(--color-text-main);
        font-size: 0.88rem;
        font-weight: var(--fw-regular);
        text-align: left;
      }

      .kind-choice.is-current {
        background: var(--control-active-fill);
        color: var(--control-active-ink);
        font-weight: var(--fw-medium);
      }

      .kind-dot {
        flex: 0 0 auto;
        width: 10px;
        height: 10px;
        border-radius: var(--radius-round);
        background: var(--kind-color, var(--map-kind-none));
      }

      .kind-dot.is-untyped {
        border: 1px solid var(--border-color);
        background: var(--map-kind-none);
      }

      .kind-choice-label {
        flex: 1 1 auto;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .kind-check {
        flex: 0 0 auto;
        color: inherit;
      }
    `,
  ],
})
export class KindChoiceListComponent {
  readonly kinds = input<readonly TopicKindDto[]>([]);
  readonly selectedId = input<string | null>(null);
  readonly includeNone = input(true);
  readonly label = input('Kind');

  readonly picked = output<string | null>();

  colorOf(kind: TopicKindDto): string {
    return kindCssVar(kind.colorKey);
  }
}
