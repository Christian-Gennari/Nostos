/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import { Pipe, PipeTransform } from '@angular/core';
import { formatNostosDate, type NostosDateMode } from '../../core/date-formats';

/**
 * `{{ note.createdAt | nostosDate }}` → "24 Oct 2026";
 * `{{ backup.createdAt | nostosDate: 'datetime' }}` → "24 Oct 2026, 14:05".
 * Use it instead of Angular's `date` pipe so every date in the app has one shape.
 */
@Pipe({
  name: 'nostosDate',
  standalone: true,
})
export class NostosDatePipe implements PipeTransform {
  transform(value: string | number | Date | null | undefined, mode: NostosDateMode = 'date'): string {
    return formatNostosDate(value, mode);
  }
}
