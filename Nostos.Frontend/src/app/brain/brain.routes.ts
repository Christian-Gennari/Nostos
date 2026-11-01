import { Routes } from '@angular/router';

import { BrainListPage } from './brain-list.page';
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
      // Lazy: the map pulls in Sigma and graphology (~570 kB), which the rest of the Brain never needs.
      { path: 'map', loadComponent: () => import('./brain-map.page').then((m) => m.BrainMapPage) },
    ],
  },
];
