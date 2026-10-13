import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { ReadAloudController } from './read-aloud-controller';

/**
 * The transport for read-aloud: shown only while a book is being read, floating
 * above the pager so it stays in reach when the rest of the chrome hides.
 */
@Component({
  selector: 'app-read-aloud-bar',
  standalone: true,
  imports: [IconButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './read-aloud-bar.component.html',
  styleUrl: './read-aloud-bar.component.css',
})
export class ReadAloudBarComponent {
  readonly controller = inject(ReadAloudController);

  readonly rateLabel = computed(() => `${this.controller.rate()}×`);
  readonly toggleLabel = computed(() => (this.controller.playing() ? 'Pause reading' : 'Resume reading'));
}
