import { bootstrapApplication } from '@angular/platform-browser';
import { pdfDefaultOptions } from 'ngx-extended-pdf-viewer';
import { appConfig } from './app/app.config';
import { App } from './app/app.component';

// Configure pdf.js once before any reader instance starts loading a document.
pdfDefaultOptions.disableAutoFetch = true;
pdfDefaultOptions.rangeChunkSize = 1024 * 1024;

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
