import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("hotspots", Path(__file__).with_name("registry-hotspots.py"))
hotspots = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hotspots)
REPO = Path(__file__).resolve().parents[1]
CONVERTED = ["docs/concepts/orchestrator-drive-to-conclusion.html",
             "docs/operations/security/overview.md", "docs/operations/setup/operator-agent.md"]
GIT_TIME = dict(os.environ, GIT_AUTHOR_DATE="2026-11-24T12:00:00Z", GIT_COMMITTER_DATE="2026-11-24T12:00:00Z")


class RegistryHotspotTests(unittest.TestCase):
    def test_three_conflict_leaders_have_bounded_append_registrations(self):
        registrations = [json.loads(p.read_text()) for p in
                         (REPO / ".agent-studio/append-only-registries").glob("*.json")]
        for path in CONVERTED:
            with self.subTest(path=path):
                rule = next((r for r in registrations if r["path"] == path), None)
                self.assertIsNotNone(rule, f"Register bounded append-only additions for {path}")
                text = (REPO / path).read_text()
                self.assertEqual(1, text.count(rule["startMarker"]))
                self.assertEqual(1, text.count(rule["endMarker"]))

    def test_weekly_guard_counts_distinct_cards_and_identifies_unhandled_registries(self):
        with tempfile.TemporaryDirectory() as folder:
            repo = Path(folder)
            path = "NewRegistration.cs"
            (repo / path).write_text("services.AddSingleton<X>();\n" * 10)
            rows = [dict(card=f"AGT-{i}", at="2026-11-24T12:00:00Z", paths=[path]) for i in range(9)]
            self.assertEqual([], hotspots.guard(rows[:8] * 2, repo, set(), 8))
            problems = hotspots.guard(rows, repo, set(), 8)
            self.assertEqual(1, len(problems))
            self.assertEqual(dict(path=path, week="2026-11-23", count=9), problems[0])
            self.assertEqual([], hotspots.guard(rows, repo, {path}, 8))
            rows[-1]["at"] = "2026-11-30T00:00:00Z"
            self.assertEqual([], hotspots.guard(rows, repo, set(), 8))

    def test_concurrent_eighth_cards_warn_after_merge_and_only_block_a_touching_review(self):
        with tempfile.TemporaryDirectory() as folder:
            repo = Path(folder)
            path = "NewRegistration.cs"
            output = repo / "report"

            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], env=GIT_TIME,
                                               stderr=subprocess.DEVNULL).decode().strip()

            def commit(message, filename, content):
                (repo / filename).write_text(content)
                git("add", "--", filename)
                git("commit", "-qm", message)

            def check(exit_code, review=True):
                env = os.environ.copy()
                if review:
                    env["AGENT_REVIEW_NAMESPACE"] = "fixture-attempt"
                else:
                    env.pop("AGENT_REVIEW_NAMESPACE", None)
                result = subprocess.run([sys.executable, hotspots.__file__, "--guard", "--repo", folder,
                                         "--output", str(output)], env=env, capture_output=True, text=True)
                self.assertEqual(exit_code, result.returncode, result.stdout + result.stderr)
                return result.stdout + result.stderr

            git("init", "-q", "-b", "develop")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            content = "services.AddSingleton<X>();\n" * 10
            commit("base", path, content)
            for card in range(1, 8):
                content += f"// {card}\n"
                commit(f"Integrate AGT-{card}", path, content)
            base = git("rev-parse", "HEAD")
            for card in [8, 9]:
                git("switch", "-qc", f"card-{card}", base)
                # Independent edits at opposite ends merge cleanly.
                change = "// first\n" + content if card == 8 else content + "// last\n"
                commit(f"AGT-{card}: change registration", path, change)
                check(0)
            git("switch", "-q", "develop")
            git("merge", "--no-ff", "-qm", "Integrate AGT-8", "card-8")
            # Exact-result review can be detached and can span several commits.
            git("update-ref", "refs/remotes/origin/develop", base)
            git("switch", "--detach", "-q")
            commit("Integrate AGT-9: change registration", path, "// first\n" + content + "// last\n")
            self.assertIn("Record the strategy", check(1))
            commit("Finish the card\n\nIntegrate the notes.\n\nAgent-Studio-Task: AGT-9", "notes.txt", "done")
            ninth = git("rev-parse", "HEAD")
            self.assertIn("9 distinct cards", check(1))
            git("switch", "-q", "develop")
            git("merge", "--no-ff", "-qm", "Integrate AGT-9", "card-9")
            self.assertIn("WARNING", check(0, review=False))
            report = json.loads((output / "registry-hotspots.json").read_text())
            self.assertEqual([path], [draft["area"] for draft in report["cardDrafts"]])
            git("switch", "--detach", "-q")
            commit("AGT-10: unrelated", "notes.txt", "unrelated")
            self.assertIn("WARNING", check(0))
            # Review clones may contain the subject history without a named target branch.
            git("update-ref", "-d", "refs/remotes/origin/develop")
            git("branch", "-D", "develop")
            check(0)
            git("checkout", "--detach", "-q", ninth)
            self.assertIn("scripts/registry-hotspots.json", check(1))

            # A real single-parent net-change receipt supplies the base in this clone.
            git("checkout", "--detach", "-q", ninth + "~2")
            commit(f"Integrate net change from {ninth[:8]}\n\nAgent-Studio-Net-Change-Of: {ninth}",
                   path, "// first\n" + content + "// last\n")
            commit("AGT-10: unrelated", "notes.txt", "unrelated")
            self.assertIn("WARNING", check(0))

    def test_ledger_filters_project_target_period_and_deduplicates_collisions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            row = dict(version=1, id="collision", project="Studio", integrationTarget="develop",
                       detectedAtUtc="2026-11-24T00:00:00Z", cardKey="AGT-1",
                       files=[dict(path="a.cs"), dict(path="a.cs")])
            (root / "one.json").write_text(json.dumps(row))
            (root / "duplicate.json").write_text(json.dumps(row))
            (root / "other.json").write_text(json.dumps(row | dict(id="other", project="Fixture")))
            (root / "bad.json").write_text("{")
            start, end = hotspots.instant("2026-11-24"), hotspots.instant("2026-11-25")
            rows, warnings = hotspots.recorded_conflicts(root, "Studio", "develop", start, end)
            self.assertEqual(1, len(rows))
            self.assertEqual([dict(path="a.cs", count=1)], hotspots.rank(rows))
            self.assertEqual(1, len(warnings))
            self.assertEqual([], hotspots.recorded_conflicts(root, "Studio", "main", start, end)[0])

    def test_integrations_measure_net_change_and_count_a_card_once(self):
        with tempfile.TemporaryDirectory() as folder:
            repo = Path(folder)
            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], stderr=subprocess.DEVNULL,
                                               env=GIT_TIME).decode().strip()
            git("init", "-q", "-b", "develop")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            (repo / "a.cs").write_text("base\n")
            git("add", ".")
            git("commit", "-qm", "base")
            git("switch", "-qc", "delivery")
            (repo / "a.cs").write_text("base\nentry\n")
            git("add", ".")
            git("commit", "-qm", "AGT-1: entry")
            git("switch", "-q", "develop")
            git("merge", "--no-ff", "-qm", "Integrate AGT-1: entry", "delivery")
            git("commit", "--allow-empty", "-qm", "Integrate AGT-1: repeated receipt")
            start, end = hotspots.instant("2026-11-24"), hotspots.instant("2026-11-27")
            rows, unknown = hotspots.integrated(repo, "HEAD", start, end)
            self.assertEqual([], unknown)
            self.assertEqual([dict(path="a.cs", count=1)], hotspots.rank(rows, True))

    def test_guard_reports_missing_source_commits_without_losing_available_attribution(self):
        with tempfile.TemporaryDirectory() as folder:
            repo = Path(folder)
            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], stderr=subprocess.DEVNULL,
                                               env=GIT_TIME).decode().strip()
            git("init", "-q", "-b", "develop")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            (repo / "a.cs").write_text("base\n")
            git("add", ".")
            git("commit", "-qm", "base")
            git("commit", "--allow-empty", "-qm", "AGT-1: available source")
            source = git("rev-parse", "HEAD")
            git("commit", "--allow-empty", "-qm", f"Integrate net change from {source[:8]}")
            # Receipt-only attribution must also work when the source object has been pruned.
            missing_source = "f" * 40
            for message in [f"Integrate net change from {missing_source[:8]}",
                            "Integrate net change\n\nAgent-Studio-Net-Change-Of: " + missing_source]:
                git("commit", "--allow-empty", "-qm", message)
            missing = git("rev-parse", "HEAD~1"), git("rev-parse", "HEAD")
            git("commit", "--allow-empty", "-qm", "Integrate net change from " + missing_source,
                "-m", "Agent-Studio-Task: AGT-2")
            git("commit", "--allow-empty", "-qm", "Integrate AGT-3: named card",
                "-m", "Agent-Studio-Net-Change-Of: " + missing_source)
            output = repo / "reports"
            result = subprocess.run(["python" if os.name == "nt" else "python3",
                                     str(REPO / "scripts/registry-hotspots.py"), "--guard",
                                     "--repo", folder, "--output", str(output)], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            report = json.loads((output / "registry-hotspots.json").read_text())
            self.assertEqual(set(missing), set(report["unattributedIntegrations"]))
            self.assertEqual({"AGT-1", "AGT-2", "AGT-3"}, {row["card"] for row in report["integrations"]})
            self.assertEqual(3, report["integratedCards"])

    def test_git_fallback_recovers_paths_from_a_resolved_sync_merge(self):
        with tempfile.TemporaryDirectory() as folder:
            repo = Path(folder)
            def git(*args):
                return subprocess.run(["git", "-C", folder, *args], capture_output=True, check=True,
                                      env=GIT_TIME).stdout.decode().strip()
            git("init", "-q", "-b", "develop")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            (repo / "registry.md").write_text("header\nfooter\n")
            git("add", ".")
            git("commit", "-qm", "base")
            git("switch", "-qc", "delivery")
            (repo / "registry.md").write_text("header\ndelivery\nfooter\n")
            git("add", ".")
            git("commit", "-qm", "AGT-2: delivery")
            git("switch", "-q", "develop")
            (repo / "registry.md").write_text("header\nintegration\nfooter\n")
            git("add", ".")
            git("commit", "-qm", "Integrate AGT-1: entry")
            git("switch", "-q", "delivery")
            merge = subprocess.run(["git", "-C", folder, "merge", "--no-commit", "develop"],
                                   capture_output=True, env=GIT_TIME)
            self.assertEqual(1, merge.returncode)
            (repo / "registry.md").write_text("header\nintegration\ndelivery\nfooter\n")
            git("add", ".")
            git("commit", "-qm", "AGT-2: merge develop")
            rows = hotspots.historical_conflicts(repo, "HEAD", hotspots.instant("2026-11-24"),
                                                 hotspots.instant("2026-11-27"))
            self.assertEqual(1, len(rows))
            self.assertEqual(["registry.md"], rows[0]["paths"])
            self.assertEqual("git-replayed-sync", rows[0]["source"])


if __name__ == "__main__":
    unittest.main()
