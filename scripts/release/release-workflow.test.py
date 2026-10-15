#!/usr/bin/env python3
"""Release publication must remain downstream of every install smoke."""

from pathlib import Path
import re
import unittest


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/release.yml"


class ReleaseWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = WORKFLOW.read_text()
        cls.jobs = {}
        for match in re.finditer(r"(?m)^  ([a-z][a-z-]+):\n", cls.source):
            end = re.search(r"(?m)^  [a-z][a-z-]+:\n", cls.source[match.end():])
            cls.jobs[match.group(1)] = cls.source[match.end():match.end() + end.start()] if end else cls.source[match.end():]

    def dependencies(self, job):
        match = re.search(r"(?m)^    needs: (.+)$", self.jobs[job])
        if not match:
            return set()
        return set(re.findall(r"[a-z][a-z-]+", match.group(1)))

    def ancestors(self, job):
        found = set()
        pending = list(self.dependencies(job))
        while pending:
            dependency = pending.pop()
            if dependency in found:
                continue
            found.add(dependency)
            pending.extend(self.dependencies(dependency))
        return found

    def test_publication_depends_on_every_install_smoke(self):
        smokes = {
            "install-smoke-linux",
            "install-smoke-linux-native",
            "install-smoke-windows",
        }
        for publisher in ("publish-images", "publish-release", "promote-images"):
            with self.subTest(publisher=publisher):
                self.assertTrue(smokes <= self.ancestors(publisher))
                self.assertIn("if: github.event_name == 'push'", self.jobs[publisher])

    def test_candidate_is_downloadable_before_publication(self):
        self.assertIn("test-build-release", self.dependencies("build-images"))
        self.assertIn("release-candidate", self.dependencies("publish-images"))
        self.assertIn("name: release-candidate-${{ github.sha }}", self.jobs["release-candidate"])
        self.assertIn("workflow_dispatch", self.source)

    def test_staged_images_are_promoted_only_after_release_creation(self):
        self.assertIn("publish-release", self.dependencies("promote-images"))
        staging = self.jobs["publish-images"]
        promotion = self.jobs["promote-images"]
        self.assertIn('docker push "$image:sha-$short_sha"', staging)
        self.assertNotIn('docker push "$image:v$VERSION"', staging)
        self.assertNotIn('docker push "$image:latest"', staging)
        self.assertIn('docker buildx imagetools create', promotion)
        self.assertIn('-t "$image:v$VERSION" -t "$image:latest"', promotion)

    def test_anonymous_pull_is_nonblocking_post_release_verification(self):
        promotion = self.jobs["promote-images"]
        check = promotion.split("- name: Check anonymous package visibility", 1)[1]
        self.assertIn("continue-on-error: true", check)
        self.assertIn("docker logout ghcr.io", check)
        self.assertIn("GITHUB_STEP_SUMMARY", check)


if __name__ == "__main__":
    unittest.main()
