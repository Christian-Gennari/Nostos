/**
 * Add Book free-source discovery (#641) at 390px. The file name starts with
 * `mobile` so `playwright.config.ts` runs it in the mobile-chromium project.
 */
import { addBookDiscoverySpecs } from './support/add-book-discovery';

addBookDiscoverySpecs('mobile');
