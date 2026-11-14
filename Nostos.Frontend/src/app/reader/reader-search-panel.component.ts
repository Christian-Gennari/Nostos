import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';
import type { ReaderSearchState } from './reader.interface';

/**
 * Search in the open book: the query, the match count and the two steps
 * through the matches. Each format owns the mechanics behind its own `IReader`
 * surface; this is the panel the shell already owned, extracted in #1607.
 */
@Component({
  selector: 'app-reader-search-panel',
  standalone: true,
  imports: [IconButtonComponent, InputDirective, SearchFieldComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-search-panel.component.html',
  styleUrl: './reader-search-panel.component.css',
  host: { style: 'display: contents' },
})
export class ReaderSearchPanel {
  readonly query = input('');
  readonly state = input<ReaderSearchState>({ status: 'idle', current: 0, total: 0 });

  readonly closed = output<void>();
  readonly queryChange = output<string>();
  readonly previous = output<void>();
  readonly next = output<void>();
}
