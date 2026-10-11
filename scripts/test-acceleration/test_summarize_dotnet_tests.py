"""Regression checks for the cross-platform console timing parser."""

import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "summarize_dotnet_tests", Path(__file__).with_name("summarize-dotnet-tests.py")
)
assert SPEC and SPEC.loader
summarizer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(summarizer)


class SummarizeDotnetTests(unittest.TestCase):
    def test_time_elapsed_accepts_msbuild_with_and_without_colon(self):
        for separator in ("", ":"):
            with self.subTest(separator=separator), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "run.log"
                path.write_text(f"Time Elapsed{separator} 00:01:02.50\n")
                _, wall, _ = summarizer.console_tests(path)
                self.assertEqual(62.5, wall)

    def test_host_load_is_optional_on_windows(self):
        for error in (AttributeError("unavailable"), OSError("unsupported")):
            with self.subTest(error=type(error).__name__), patch.object(
                summarizer.os, "getloadavg", side_effect=error
            ):
                self.assertIsNone(summarizer.host_load1())


if __name__ == "__main__":
    unittest.main()
