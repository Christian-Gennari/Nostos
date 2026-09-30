/**
 * In-book Light/Dark switch (#651) at 390px. The file name starts with `mobile`
 * so `playwright.config.ts` runs it in the mobile-chromium project.
 */
import { readerThemeSwitchSpecs } from './support/reader-theme-switch';

readerThemeSwitchSpecs('mobile');
