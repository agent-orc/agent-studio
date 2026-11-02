import importlib.util
import pathlib
import tempfile
import unittest
from unittest import mock


SCRIPT = pathlib.Path(__file__).with_name("repository_posture.py")
spec = importlib.util.spec_from_file_location("repository_posture", SCRIPT)
posture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(posture)


class RepositoryPostureTests(unittest.TestCase):
    def test_action_tag_and_mutable_image_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            workflow = root / ".github" / "workflows" / "test.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("jobs:\n  test:\n    steps:\n      - uses: actions/checkout@v4\n    container:\n      image: node:22-alpine\n    strategy:\n      matrix:\n        image: ['alpine:3']\n")
            findings = posture.scan_checkout(root)
            self.assertEqual({"unpinned-action", "unpinned-image"}, {f["code"] for f in findings})
            self.assertEqual(2, sum(f["code"] == "unpinned-image" for f in findings))

    def test_required_rules_refuse_force_push_and_foreign_actor(self):
        findings = posture.check_rulesets([], platform_app_id=42)
        self.assertIn("missing-repository-rules", {f["code"] for f in findings})

        rules = posture.expected_rulesets(42, ["gate"])
        self.assertEqual([], posture.check_rulesets(rules, 42))
        integrity = [r for r in rules if r["name"].endswith("integrity")]
        self.assertTrue(all(not r["bypass_actors"] for r in integrity))
        self.assertTrue(all({"deletion", "non_fast_forward"} <= {rule["type"] for rule in r["rules"]}
                            for r in integrity))
        publisher = [r for r in rules if r["name"].endswith("publisher")]
        self.assertTrue(all(r["bypass_actors"] == [{"actor_id": 42, "actor_type": "Integration", "bypass_mode": "always"}]
                            for r in publisher))
        rules[0]["rules"].remove({"type": "non_fast_forward"})
        self.assertIn("repository-rules-drift", {f["code"] for f in posture.check_rulesets(rules, 42)})
        self.assertIn("agent-in-bypass", {f["code"] for f in posture.check_rulesets(rules, 42, agent_app_id=42)})

    def test_public_url_requires_tls_and_security_headers(self):
        self.assertEqual("tls-invalid", posture.check_public_url("http://site.invalid")[0]["code"])
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.geturl.return_value = "https://site.invalid"
        response.headers = {"strict-transport-security": "max-age=31536000"}
        with mock.patch.object(posture.urllib.request, "urlopen", return_value=response):
            findings = posture.check_public_url("https://site.invalid")
        self.assertEqual(len(posture.REQUIRED_HEADERS) - 1, len(findings))
        self.assertEqual({"missing-response-header"}, {f["code"] for f in findings})


if __name__ == "__main__":
    unittest.main()
