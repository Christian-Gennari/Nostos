/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** Whether the keyboard's command key is ⌘ (Apple devices) rather than Ctrl. */
export function isApplePlatform(userAgent: string | undefined = typeof navigator === 'undefined' ? undefined : navigator.userAgent): boolean {
  return !!userAgent && /Macintosh|Mac OS X|iPhone|iPad|iPod/i.test(userAgent);
}

/** The modifier a shortcut sheet shows for "Cmd/Ctrl": ⌘ on Apple devices, Ctrl elsewhere. */
export function modifierKeyLabel(userAgent?: string): string {
  return isApplePlatform(userAgent) ? '⌘' : 'Ctrl';
}
