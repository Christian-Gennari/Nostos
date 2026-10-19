import { Routes } from '@angular/router';
import { WorkspaceLayout } from './layout/workspace-layout/workspace-layout.component';
import { secondBrainRedirect } from './brain/brain-redirect';

export const routes: Routes = [
  {
    path: 'start',
    loadComponent: () =>
      import('./cloud-entry/cloud-start.component').then((m) => m.CloudStartComponent),
  },
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'library',
  },

  // Lightweight in-app reference fixture for Nostos UI v1. Intentionally not
  // linked from product navigation; it exists for implementation and visual QA.
  {
    path: 'ui-catalogue',
    loadComponent: () =>
      import('./ui/ui-catalogue/ui-catalogue.component').then((m) => m.UiCatalogueComponent),
  },

  // Sort one by one: a whole screen for one unsorted note, outside the dock.
  {
    path: 'brain/unsorted/sort',
    loadComponent: () => import('./brain/brain-sort.page').then((m) => m.BrainSortPage),
  },

  // Dedicated Reader Route (full-screen immersive mode)
  {
    path: 'read/:id',
    loadComponent: () => import('./reader/reader-shell.component').then((m) => m.ReaderShell),
  },

  // Wrap ONLY library + brain inside workspace layout
  {
    path: '',
    component: WorkspaceLayout,
    children: [
      {
        path: 'library',
        loadComponent: () => import('./library/library.component').then((m) => m.Library),
      },
      // The Brain (docs/brain.md): places, topics and the map.
      {
        path: 'brain',
        loadChildren: () => import('./brain/brain.routes').then((m) => m.BRAIN_ROUTES),
      },
      // The old Brain's address: saved links and history land in the new one.
      { path: 'second-brain', pathMatch: 'full', redirectTo: secondBrainRedirect },
      {
        path: 'studio',
        loadComponent: () =>
          import('./writing-studio/writing-studio.component').then((m) => m.WritingStudio),
      },
      // Books the user wants and does not have yet. Before `library/:id` so
      // the literal segment always wins.
      {
        path: 'library/to-read',
        loadComponent: () =>
          import('./reading-list/reading-list.component').then((m) => m.ReadingList),
      },
      {
        path: 'library/:id',
        loadComponent: () =>
          import('./book-detail/book-detail.component').then((m) => m.BookDetail),
      },
      {
        path: 'settings',
        children: [
          {
            path: 'library',
            data: { manageLibraryPage: true },
            loadComponent: () =>
              import('./settings/settings.component').then((m) => m.SettingsComponent),
          },
          {
            path: '',
            pathMatch: 'full',
            loadComponent: () =>
              import('./settings/settings.component').then((m) => m.SettingsComponent),
          },
        ],
      },
    ],
  },

  // Catch-all: redirect unknown routes to library
  {
    path: '**',
    redirectTo: 'library',
  },
];
