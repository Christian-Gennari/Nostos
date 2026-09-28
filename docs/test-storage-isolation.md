# Backend test storage isolation

Backend tests must never write book or media fixtures into the application's normal
`Nostos.Backend/Storage/books` tree.

Real `WebApplicationFactory<Program>` hosts use an explicit
`Storage:BooksRoot` under the operating system temporary directory and delete
that temporary root when the factory is disposed. `FileStorageService` also
fails closed in the `Testing` environment when `Storage:BooksRoot` is missing,
so a new test host cannot silently fall back to the real/default library path.

## One-time cleanup of old leaked fixtures

Older test runs could leave GUID-named directories under
`Nostos.Backend/Storage/books`. Do **not** delete that directory wholesale:
real self-hosted books use the same layout.

1. Stop any local Nostos process that could be writing to the library.
2. List candidate directories and their timestamps without deleting anything.

   PowerShell:

   ```powershell
   Get-ChildItem Nostos.Backend/Storage/books -Directory |
     Select-Object Name, LastWriteTime
   ```

   Bash:

   ```bash
   find Nostos.Backend/Storage/books -mindepth 1 -maxdepth 1 -type d -printf '%f %TY-%Tm-%Td %TH:%TM:%TS\n'
   ```

3. Inspect each candidate. Treat a directory as a test fixture only when it can
   be positively tied to a known test run or fixture: for example, its book ID
   is from a test database/log, its contents are a known synthetic fixture, or
   its creation/modification time matches a recorded isolated verification run.
   A GUID-shaped directory name by itself is **not** evidence that it is safe to
   remove.
4. Delete only the exact directories you have confirmed are test fixtures.

   PowerShell:

   ```powershell
   Remove-Item -Recurse -Force Nostos.Backend/Storage/books/<confirmed-test-book-id>
   ```

   Bash:

   ```bash
   rm -rf -- Nostos.Backend/Storage/books/<confirmed-test-book-id>
   ```

5. Leave uncertain directories untouched. There is intentionally no automatic
   cleanup routine for unknown existing book folders.

This cleanup is only for artifacts leaked by historical test runs. It is not a
production storage migration and must never be generalized to delete unreferenced
or old-looking user books.
