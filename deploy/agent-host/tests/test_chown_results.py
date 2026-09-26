import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


HELPER = Path(__file__).resolve().parents[1] / "agent-runner-deploy"


class ChownResultsTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("fakeroot"), "fakeroot is required for the foreign-owner fixture")
    def test_foreign_owned_result_is_repaired_and_other_task_is_untouched(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            results = root / "AGT-2737" / "results"
            results.mkdir(parents=True)
            foreign = results / "report.html"
            foreign.write_text("container output")
            other = root / "AGT-other" / "results"
            other.mkdir(parents=True)
            untouched = other / "report.html"
            untouched.write_text("other output")
            patched = root / "helper"
            patched.write_text(HELPER.read_text().replace(
                'readonly coding_results_root="/var/lib/agent-runner/work/tasks"',
                f'readonly coding_results_root="{root}"',
            ))
            script = """
                set -euo pipefail
                source "$1"
                logger() { :; }
                chown 10001:10001 "$2"
                chown 10001:10001 "$3"
                [[ "$(stat -c %u "$2")" == 10001 ]]
                chown_results_from_stdin <<< 'AGT-2737'
                [[ "$(stat -c %u "$2")" == 1000 ]]
                [[ "$(stat -c %u "$3")" == 10001 ]]
                if (chown_results_from_stdin <<< '../AGT-other') 2>/dev/null; then
                  exit 1
                fi
                [[ "$(stat -c %u "$3")" == 10001 ]]
            """
            result = subprocess.run(
                ["fakeroot", "bash", "-c", script, "bash", str(patched), str(foreign), str(untouched)],
                capture_output=True, text=True, check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)


if __name__ == "__main__":
    unittest.main()
