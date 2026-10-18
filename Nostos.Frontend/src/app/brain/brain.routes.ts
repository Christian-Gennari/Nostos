import { Routes } from '@angular/router';

import { BrainListPage } from './brain-list.page';
import { BrainMapPage } from './brain-map.page';
import { BrainNotePage } from './brain-note.page';
import { BrainShellComponent } from './brain-shell.component';

/** The Brain's places, inside its shell (sidebar + page). See docs/brain.md. */
export const BRAIN_ROUTES: Routes = [
  {
    path: '',
    component: BrainShellComponent,
    children: [
      { path: '', component: BrainListPage, data: { place: 'all' } },
      { path: 'passages', component: BrainListPage, data: { place: 'passages' } },
      { path: 'unsorted', component: BrainListPage, data: { place: 'unsorted' } },
      { path: 'topics/:topicId', component: BrainListPage, data: { place: 'topic' } },
      { path: 'notes/:noteId', component: BrainNotePage },
      { path: 'map', component: BrainMapPage },
    ],
  },
];
