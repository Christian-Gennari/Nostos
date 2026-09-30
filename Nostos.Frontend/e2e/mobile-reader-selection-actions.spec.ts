/**
 * EPUB in-text selection actions (#650) at 390px with touch. The file name
 * starts with `mobile` so `playwright.config.ts` runs it in mobile-chromium.
 */
import { mobileSelectionSpecs } from './support/reader-selection-actions';

mobileSelectionSpecs();
