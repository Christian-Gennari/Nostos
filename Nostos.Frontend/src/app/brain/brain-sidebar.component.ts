import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

import type { TopicDto } from '../core/services/topics.service';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { BrandLockupComponent } from '../ui/brand-lockup/brand-lockup.component';
import { InlineRenameComponent } from '../ui/inline-rename/inline-rename.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';
import { isTopicSort } from './brain.helpers';

/**
 * The Brain's sidebar: the fixed places (All notes, Passages, Unsorted, Map)
 * with their counts, then every topic. Built like the Library's sidebar, with
 * topics in the place collections have there.
 */
@Component({
  selector: 'app-brain-sidebar',
  standalone: true,
  imports: [
    RouterLink,
    RouterLinkActive,
    DropdownComponent,
    IconButtonComponent,
    NostosIconComponent,
    BrandLockupComponent,
    InlineRenameComponent,
    EmptyStateComponent,
  ],
  templateUrl: './brain-sidebar.component.html',
  styleUrls: ['./brain-sidebar.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainSidebarComponent {
  readonly state = inject(BrainStateService);
  readonly actions = inject(BrainTopicActionsService);

  readonly sortOptions = [
    { value: 'usage', label: 'Most used' },
    { value: 'az', label: 'A–Z' },
  ] satisfies readonly DropdownOption[];

  setSort(value: string): void {
    if (isTopicSort(value)) this.state.setTopicSort(value);
  }

  isRenaming(topic: TopicDto): boolean {
    return this.actions.renameId() === topic.id && this.actions.renameSurface() === 'sidebar';
  }
}
