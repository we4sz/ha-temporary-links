#!/usr/bin/env python3
"""Self-proof (E1.S11.A2/A3): the facit CLI verifies ITSELF.

Runs the CLI's own test suite under pytest IN-PROCESS (FACIT_INPROCESS=1) with per-test
coverage of facit.py, emits junit results, then runs `prove` + `conform` on the CLI's own
facit (tools/facit/spec) using that evidence — so every CLI AC marked proven is backed by a
green test that actually executes facit.py, and is conform-clean.

Run: python3 tools/facit/selfproof.py
Exit 0 only when the suite is green, prove records no unexpected refusals, and conform is clean.
"""
import os
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPEC = "tools/facit/spec"
FACIT = "tools/facit/facit.py"
IMPL = os.path.join(SPEC, "implementation.json")


def _verdict(tests_rc, prove_rc, conform_rc):
    """Pure self-proof decision (E1.S11.A4): the orchestrator fails if ANY stage failed —
    including prove. The prove return code must NOT be discarded (that was the SP-1 blind
    spot: a prove refusal was silently ignored, so a broken binding could still self-prove).
    Returns (exit_code, message)."""
    if tests_rc != 0:
        return 1, "the CLI's own test suite is not green"
    if prove_rc != 0:
        return 1, "prove refused a CLI acceptance criterion (binding not green or not coverage-backed)"
    if conform_rc != 0:
        return 1, "the CLI's own facit has drifted (re-run after re-proving)"
    return 0, "the tool proved itself, coverage-backed and conform-clean"


def main():
    with tempfile.TemporaryDirectory() as tmp:
        cov = os.path.join(tmp, ".coverage")
        junit = os.path.join(tmp, "junit.xml")
        rc_file = os.path.join(tmp, "coveragerc")
        with open(rc_file, "w") as f:
            f.write("[run]\nsource = tools/facit\nomit = */tests/*\n")

        env = {**os.environ, "FACIT_INPROCESS": "1", "COVERAGE_FILE": cov}

        print("[selfproof] running the CLI test suite in-process with per-test coverage…")
        tests = subprocess.run(
            [sys.executable, "-m", "pytest", "tools/facit/tests/test_facit.py",
             "--cov=tools/facit", "--cov-config=" + rc_file, "--cov-context=test",
             "--cov-report=", "--junitxml=" + junit, "-p", "no:cacheprovider", "-q"],
            cwd=REPO_ROOT, env=env)
        if tests.returncode != 0:
            print("[selfproof] FAIL — the CLI's own test suite is not green")
            return 1

        print("[selfproof] certifying the CLI's own facit (prove)…")
        prove = subprocess.run(
            [sys.executable, FACIT, "--root", SPEC, "prove",
             "--impl", IMPL, "--results", junit, "--coverage", cov, "--src-root", "tools/facit"],
            cwd=REPO_ROOT)

        print("[selfproof] checking code-drift (conform)…")
        conform = subprocess.run(
            [sys.executable, FACIT, "--root", SPEC, "conform"], cwd=REPO_ROOT)

        # E1.S11.A4 / SP-1: do NOT discard prove's return code — a refusal must fail the self-proof.
        rc, msg = _verdict(tests.returncode, prove.returncode, conform.returncode)
        print(f"[selfproof] {'OK' if rc == 0 else 'FAIL'} — {msg}")
        return rc


if __name__ == "__main__":
    sys.exit(main())
