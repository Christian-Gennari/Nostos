/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { Directive } from '@angular/core';

/**
 * Canonical Nostos checkbox and radio.
 *
 * Like appInput, these keep the NATIVE input as the host: checked, disabled,
 * name, (change), keyboard, form participation and labelling all stay the
 * browser's and the caller's. The directive only adds the class that styles.css
 * draws, because a directive cannot own an encapsulated stylesheet and an
 * <input> cannot host a component template.
 *
 * Before this, every checkbox was the browser's own with `accent-color` set to
 * whichever token that surface picked (--color-primary in three places,
 * --selection-surface in one), so a checked box was a different shape and
 * colour from the switch beside it and from each other. Now checked is the
 * switch's forest fill with the on-primary tick, in both themes.
 */
@Directive({
  selector: 'input[type=checkbox][appCheckbox]',
  standalone: true,
  host: { class: 'nostos-check nostos-check--box' },
})
export class CheckboxDirective {}

@Directive({
  selector: 'input[type=radio][appRadio]',
  standalone: true,
  host: { class: 'nostos-check nostos-check--radio' },
})
export class RadioDirective {}
