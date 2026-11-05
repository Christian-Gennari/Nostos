export enum BookFilter {
  All = '', // maps to empty string for API
  Favorites = 'favorites',
  Finished = 'finished',
  Reading = 'reading',
  Unsorted = 'unsorted',
  NotStarted = 'notstarted',
}

export enum BookSort {
  Recent = 'recent',
  Title = 'title',
  Rating = 'rating',
  LastRead = 'lastread',
  RecentlyFinished = 'recentlyfinished',
}
