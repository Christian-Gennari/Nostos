import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ButtonComponent } from '../ui/button/button.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';

/**
 * What an address that matches no page shows (issue #1440): one quiet line and
 * a way back, instead of a silent redirect that hides the mistake.
 */
@Component({
  selector: 'nostos-not-found',
  standalone: true,
  imports: [EmptyStateComponent, ButtonComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="not-found">
      <nostos-empty-state message="Page not found." detail="This link does not lead to anything in Nostos.">
        <a appButton variant="secondary" size="sm" routerLink="/library">Go to the Library</a>
      </nostos-empty-state>
    </main>
  `,
  styles: `
    .not-found {
      display: grid;
      min-height: 60vh;
      place-items: center;
    }
  `,
})
export class NotFoundComponent {}
