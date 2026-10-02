"""Exercise editorial rules and real git histories without GitHub credentials."""

import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "generate-release-notes.py"
spec = importlib.util.spec_from_file_location("release_notes", SCRIPT)
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)


def change(title, labels=(), body="", **extra):
    return {"title": title, "labels": [{"name": label} for label in labels], "body": body, **extra}


class EditorialTests(unittest.TestCase):
    def test_scopes_and_public_types(self):
        for scope, area in (("reader", "reader"), ("notes", "reader"), ("brain", "reader"),
                            ("library", "library"), ("opds", "library"),
                            ("assistant", "assistant"), ("ask-nostos", "assistant"), ("ui", "other")):
            for kind in ("feat", "fix", "perf", "ux"):
                with self.subTest(scope=scope, kind=kind):
                    self.assertEqual(notes.entry(change(f"{kind}({scope}): save your place")),
                                     (area, "Save your place."))

    def test_labels_override_scopes_and_support_plain_titles(self):
        self.assertEqual(notes.entry(change("fix(ui): find books", ["area: library"])),
                         ("library", "Find books."))
        self.assertEqual(notes.entry(change("feat(reader): save a thought", ["area:assistant"])),
                         ("assistant", "Save a thought."))
        self.assertEqual(notes.entry(change("Save a thought", ["area: reading"])),
                         ("reader", "Save a thought."))
        self.assertEqual(notes.entry(change("Settings improvements", ["release-note"])),
                         ("other", "Settings improvements."))
        self.assertIsNone(notes.entry(change("Unlabelled internal work")))

    def test_noise_is_omitted_even_when_it_has_an_area_label(self):
        for title in ("chore(reader): update dependencies", "test(notes): cover notes",
                      "ci: fix workflow", "docs(reader): update guide", "refactor(library): move service",
                      "style: formatting", "build: packages", "fix(test): isolate fixtures",
                      "fix(deps): update package", "fix(quality-bed): update gate", "fix(release): tags",
                      "feat(releases): add notes generator"):
            with self.subTest(title=title):
                self.assertIsNone(notes.entry(change(title, ["area: reading"])))

    def test_explicit_skip_wins_over_area_and_body(self):
        for label in notes.SKIP_LABELS:
            self.assertIsNone(notes.entry(change("feat(reader): read books", [label, "area: reading"],
                                                "## Release notes\n\nRead your books.")))

    def test_release_paragraph_beats_summary_and_title(self):
        body = "## Summary\n\nTechnical summary.\n\n## Release notes\n\nYour book keeps\nyour place.\n\n## Tests\n\nInternal details."
        self.assertEqual(notes.entry(change("fix(reader): CFI persistence", body=body)),
                         ("reader", "Your book keeps your place."))

    def test_marker_beats_heading(self):
        body = "<!-- release-note -->\nRead at your own pace.\n<!-- /release-note -->\n## Release notes\n\nOther wording."
        self.assertEqual(notes.entry(change("feat(reader): typeface", body=body)),
                         ("reader", "Read at your own pace."))

    def test_summary_uses_prose_and_ignores_verification(self):
        body = "## Summary\n\nRead offline.\n\nVerified: 50 tests passed.\n## Tests\n\nMore tests."
        self.assertEqual(notes.entry(change("feat(reader): cache", body=body)),
                         ("reader", "Read offline."))
        for body in ("## Summary\n\n- Internal list", "## Summary\n\n| A | B |",
                     "## Tests\n\n50 tests.", "Technical PR introduction."):
            self.assertEqual(notes.entry(change("feat(reader): read offline", body=body)),
                             ("reader", "Read offline."))

    def test_breaking_prefix_pr_suffix_and_link(self):
        self.assertEqual(notes.entry(change("feat(reader)!: read offline (#32)", number=32,
                                           url="https://github.com/example/nostos/pull/32")),
                         ("reader", "Read offline. ([#32](https://github.com/example/nostos/pull/32))"))

    def test_empty_sections_deduplication_and_order(self):
        rendered = notes.render([change("fix(ui): clearer settings"), change("feat(reader): read offline"),
                                 change("feat(reader): read offline"), change("test: checks")],
                                "Unreleased", "a" * 40, "b" * 40, "example/nostos")
        self.assertEqual(rendered.count("- Read offline."), 1)
        self.assertLess(rendered.index("Reader & Notes"), rendered.index("Improvements & Fixes"))
        self.assertNotIn("Library & Discovery", rendered)
        self.assertNotIn("test:", rendered)
        self.assertIn("compare/" + "a" * 40 + "..." + "b" * 40, rendered)

    def test_no_reader_changes_is_explicit(self):
        rendered = notes.render([change("chore: work")], "Unreleased", "a", "b", None)
        self.assertIn("No reader-facing changes", rendered)
        self.assertNotIn("###", rendered)

    def test_calver_calendar_and_same_day_suffix(self):
        for version in ("Unreleased", "v2026.10.02", "v2026.10.02.1", "v2026.10.02.10", "v2028.02.29"):
            self.assertEqual(notes.validate_version(version), version)
        for version in ("v1.2.3", "2026.10.02", "v2026.1.02", "v2026.02.29", "v2026.13.01",
                        "v2026.10.02.0", "v2026.10.02.01", "v2026.10.02-beta", "v0000.01.01"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                notes.validate_version(version)


class ChangelogTests(unittest.TestCase):
    def test_refreshing_an_older_release_keeps_its_position(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "CHANGELOG.md"
            notes.update_changelog(path, "## v2026.10.01\n\nOld copy.\n", "v2026.10.01")
            notes.update_changelog(path, "## v2026.10.02\n\nNew copy.\n", "v2026.10.02")
            notes.update_changelog(path, "## v2026.10.01\n\nCorrected copy.\n", "v2026.10.01")
            self.assertLess(path.read_text().index("## v2026.10.02"), path.read_text().index("## v2026.10.01"))
            self.assertIn("Corrected copy", path.read_text())
            self.assertNotIn("Old copy", path.read_text())

    def test_create_rerun_and_preserve_older_editorial_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "CHANGELOG.md"
            notes.update_changelog(path, "## v2026.10.01\n\nReviewed older copy.\n", "v2026.10.01")
            old = path.read_text().split("## v2026.10.01", 1)[1]
            new = "## v2026.10.02\n\nNew copy.\n"
            notes.update_changelog(path, new, "v2026.10.02")
            first = path.read_bytes()
            notes.update_changelog(path, new, "v2026.10.02")
            self.assertEqual(path.read_bytes(), first)
            self.assertEqual(path.read_text().count("## v2026.10.02"), 1)
            self.assertEqual(path.read_text().split("## v2026.10.01", 1)[1], old)
            self.assertLess(path.read_text().index("## v2026.10.02"), path.read_text().index("## v2026.10.01"))

    def test_release_replaces_pending_draft_but_keeps_history(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "CHANGELOG.md"
            notes.update_changelog(path, "## v2026.10.01\n\nOld copy.\n", "v2026.10.01")
            notes.update_changelog(path, "## Unreleased\n\nDraft copy.\n", "Unreleased")
            notes.update_changelog(path, "## v2026.10.02\n\nReviewed copy.\n", "v2026.10.02")
            self.assertNotIn("Unreleased\n", path.read_text())
            self.assertNotIn("Draft copy", path.read_text())
            self.assertIn("Old copy", path.read_text())


class HistoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q", "--initial-branch=main")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.base = self.commit("feat(reader): old change")
        self.git("tag", "v2026.10.01")
        self.git("switch", "-q", "-c", "reader")
        self.commit("feat(reader): read offline")
        self.commit("test(reader): check cache")
        self.git("switch", "-q", "main")
        self.git("merge", "--no-ff", "reader", "-m", "Merge pull request #1 from example/reader")
        self.merge = self.git("rev-parse", "HEAD")
        self.git("switch", "-q", "-c", "library")
        self.commit("feat(library): find books")
        self.git("switch", "-q", "main")
        self.git("merge", "--squash", "library")
        self.squash = self.commit("ux(library): find books (#2)")
        self.target = self.commit("fix(ui): clearer settings")
        self.after = self.commit("feat(assistant): later change")

    def git(self, *args):
        return notes.run("git", *args, root=self.root)

    def commit(self, title):
        path = self.root / "content.txt"
        path.write_text((path.read_text() if path.exists() else "") + title + "\n")
        self.git("add", "content.txt")
        self.git("commit", "-q", "-m", title)
        return self.git("rev-parse", "HEAD")

    def test_git_mode_uses_tag_and_pin_boundaries_through_merges(self):
        start, end = notes.resolve_range("v2026.10.01", self.target, root=self.root)
        rendered = notes.render(notes.commits(start, end, first_parent=False, root=self.root),
                                "Unreleased", start, end, None)
        self.assertIn("Read offline.", rendered)
        self.assertIn("Find books.", rendered)
        self.assertIn("Clearer settings.", rendered)
        for unwanted in ("Old change", "Later change", "Check cache", "Merge pull request"):
            self.assertNotIn(unwanted, rendered)

    def test_divergent_and_reversed_pins_fail(self):
        self.git("switch", "-q", "-c", "divergent", self.base)
        other = self.commit("feat(notes): other branch")
        for start, end in ((other, self.target), (self.target, self.base)):
            with self.assertRaises(ValueError):
                notes.resolve_range(start, end, root=self.root)

    def test_github_mode_fetches_each_exact_pr_once(self):
        real_run = notes.run
        calls = []

        def fetch(*args, root):
            if args[0] != "gh":
                return real_run(*args, root=root)
            calls.append(args)
            number = int(args[-1].rsplit("/", 1)[1])
            return json.dumps({"merged_at": "2026-10-02T12:00:00Z",
                               "merge_commit_sha": self.merge if number == 1 else self.squash,
                               "title": "feat(ui): read offline" if number == 1 else "ux(library): find books",
                               "labels": [{"name": "area: reading"}] if number == 1 else [],
                               "body": "## Release notes\n\nYour books travel with you." if number == 1 else None})

        with patch.object(notes, "run", side_effect=fetch):
            changes = notes.github_changes(self.base, self.target, "example/nostos", root=self.root)
        self.assertEqual(len(calls), 2)
        self.assertEqual([c.get("number") for c in changes], [1, 2, None])
        rendered = notes.render(changes, "Unreleased", self.base, self.target, "example/nostos")
        self.assertEqual(rendered.count("Your books travel with you."), 1)
        self.assertIn("### 📖 Reader & Notes", rendered)
        self.assertIn("/pull/1", rendered)
        self.assertIn("Clearer settings.", rendered)

    def test_metadata_failure_does_not_silently_fall_back(self):
        real_run = notes.run

        def fetch(*args, root):
            if args[0] != "gh":
                return real_run(*args, root=root)
            return json.dumps({"merged_at": None, "merge_commit_sha": self.merge})

        with patch.object(notes, "run", side_effect=fetch), self.assertRaisesRegex(ValueError, "does not match"):
            notes.github_changes(self.base, self.target, "example/nostos", root=self.root)

    def test_cli_preview_write_and_invalid_input_preserves_file(self):
        scripts = self.root / "scripts"
        scripts.mkdir()
        shutil.copyfile(SCRIPT, scripts / SCRIPT.name)
        command = [sys.executable, str(scripts / SCRIPT.name), "--source", "git", "--from",
                   "v2026.10.01", "--to", self.target, "--version", "v2026.10.02"]
        result = subprocess.run(command, cwd=self.root, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(result.stdout.startswith("## v2026.10.02"))
        self.assertFalse((self.root / "CHANGELOG.md").exists())
        result = subprocess.run(command + ["--write", "CHANGELOG.md"], cwd=self.root, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "")
        path = self.root / "CHANGELOG.md"
        before = path.read_bytes()
        invalid = command.copy()
        invalid[invalid.index("v2026.10.02")] = "v2026.02.30"
        result = subprocess.run(invalid + ["--write", "CHANGELOG.md"], cwd=self.root, text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
