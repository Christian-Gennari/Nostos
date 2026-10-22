"""Exercise ensure-release.sh against a real git history with stubbed docker and gh."""

import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "ensure-release.sh"
VERSION = "v2026.10.25"

DOCKER_STUB = textwrap.dedent(
    """\
    #!/usr/bin/env bash
    # docker buildx imagetools inspect <ref> --format ...
    case "$4" in
      *:selfhosted-sha-*) digest="$STUB_SOURCE_DIGEST" ;;
      *) digest="$STUB_RELEASE_DIGEST" ;;
    esac
    [ -n "$digest" ] || exit 1
    printf '{"digest":"%s"}' "$digest"
    """
)

GH_STUB = textwrap.dedent(
    """\
    #!/usr/bin/env bash
    # gh release view <version> succeeds only when the release exists.
    [ "$1 $2" = "release view" ] && [ "$STUB_RELEASE_EXISTS" = "1" ]
    """
)


def git(cwd, *args):
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True
    ).stdout.strip()


class EnsureReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.repo = root / "repo"
        self.repo.mkdir()
        git(self.repo, "init", "-q")
        git(self.repo, "config", "user.name", "Test")
        git(self.repo, "config", "user.email", "test@example.com")
        git(self.repo, "commit", "-q", "--allow-empty", "-m", "first")
        self.first = git(self.repo, "rev-parse", "HEAD")
        git(self.repo, "commit", "-q", "--allow-empty", "-m", "second")
        self.second = git(self.repo, "rev-parse", "HEAD")

        self.bin = root / "bin"
        self.bin.mkdir()
        for name, body in (("docker", DOCKER_STUB), ("gh", GH_STUB)):
            path = self.bin / name
            path.write_text(body)
            path.chmod(0o755)
        self.notes = root / "notes.md"
        self.notes.write_text("notes\n")

    def tearDown(self):
        self.temp.cleanup()

    def run_script(self, revision, source="sha256:aaa", release="", release_exists=False):
        env = {
            **os.environ,
            "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}",
            "DRY_RUN": "1",
            "STUB_SOURCE_DIGEST": source,
            "STUB_RELEASE_DIGEST": release,
            "STUB_RELEASE_EXISTS": "1" if release_exists else "0",
        }
        result = subprocess.run(
            ["bash", str(SCRIPT), VERSION, revision, self.first, str(self.notes)],
            cwd=self.repo, env=env, capture_output=True, text=True,
        )
        writes = [line.removeprefix("DRY RUN: ") for line in result.stdout.splitlines()
                  if line.startswith("DRY RUN: ")]
        return result, writes

    def tag(self, revision):
        git(self.repo, "tag", "-a", VERSION, revision, "-m", "release")

    def test_new_release_tags_image_before_publishing_release(self):
        result, writes = self.run_script(self.second)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(writes), 4)
        self.assertIn(f"tag -a {VERSION} {self.second}", writes[0])
        self.assertIn(f"push origin refs/tags/{VERSION}", writes[1])
        self.assertTrue(writes[2].startswith("docker buildx imagetools create"))
        self.assertIn("@sha256:aaa", writes[2])
        self.assertTrue(writes[3].startswith(f"gh release create {VERSION} --verify-tag"))

    def test_complete_release_writes_nothing(self):
        self.tag(self.second)
        result, writes = self.run_script(
            self.second, release="sha256:aaa", release_exists=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(writes, [])

    def test_repairs_missing_image_tag_and_release(self):
        self.tag(self.second)
        result, writes = self.run_script(self.second)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(writes), 2)
        self.assertTrue(writes[0].startswith("docker buildx imagetools create"))
        self.assertTrue(writes[1].startswith("gh release create"))

    def test_repairs_missing_image_tag_only(self):
        self.tag(self.second)
        result, writes = self.run_script(self.second, release_exists=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(writes), 1)
        self.assertTrue(writes[0].startswith("docker buildx imagetools create"))

    def test_existing_tag_on_another_revision_fails_without_writes(self):
        self.tag(self.first)
        result, writes = self.run_script(self.second)
        self.assertEqual(result.returncode, 1)
        self.assertIn("never move", result.stderr)
        self.assertEqual(writes, [])

    def test_image_tag_with_another_digest_fails_without_writes(self):
        result, writes = self.run_script(self.second, release="sha256:bbb")
        self.assertEqual(result.returncode, 1)
        self.assertIn("sha256:bbb", result.stderr)
        self.assertEqual(writes, [])

    def test_missing_image_fails_before_tagging(self):
        result, writes = self.run_script(self.second, source="")
        self.assertEqual(result.returncode, 1)
        self.assertIn("No published image", result.stderr)
        self.assertEqual(writes, [])

    def test_release_image_tag_without_revision_tag_is_enough(self):
        self.tag(self.second)
        result, writes = self.run_script(
            self.second, source="", release="sha256:bbb", release_exists=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(writes, [])


if __name__ == "__main__":
    unittest.main()
