#!/usr/bin/env python3
"""Tests for tools/facit/facit.py — runnable as: python3 tools/facit/tests/test_facit.py

All tests operate only on tools/facit/spec (the CLI's own facit) or temporary directories.
They never mutate docs/facit/ or docs/facit/facit.lock.json.

Coverage:
  - E1.S1/S2/S4: compile + validate succeed on docs/facit and tools/facit/spec
  - E1.S6.A3: lock then diff on tools/facit/spec is clean
  - E1.S6.A2: editing one AC text in a temp copy makes diff report exactly that node
  - E1.S5.A2: lock refuses on an invalid facit (broken facit.json)
  - E1.S5.A3: re-lock carries proven status forward only for unchanged nodes
  - E1.S11.A1: prove accepts pytest/junit results in addition to VSTest TRX
"""
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
FACIT_PY = os.path.join(REPO_ROOT, "tools", "facit", "facit.py")
CLI_SPEC_DIR = os.path.join(REPO_ROOT, "tools", "facit", "spec")
DOCS_FACIT_DIR = os.path.join(REPO_ROOT, "docs", "facit")
DOCS_LOCK = os.path.join(DOCS_FACIT_DIR, "facit.lock.json")
CLI_LOCK = os.path.join(CLI_SPEC_DIR, "facit.lock.json")

# Cached facit module for in-process mode (loaded once per test run).
_INPROCESS_FACIT_MODULE = None


def _get_inprocess_module():
    """Return the cached facit module, loading it on first call."""
    global _INPROCESS_FACIT_MODULE
    if _INPROCESS_FACIT_MODULE is None:
        _INPROCESS_FACIT_MODULE = _import_facit_module()
    return _INPROCESS_FACIT_MODULE


def run(args, *, check=False, capture=True):
    """Run facit.py with the given args. Returns (returncode, stdout+stderr).

    When env var FACIT_INPROCESS=1 is set, invokes facit in-process instead of
    via subprocess so that coverage.py can measure facit.py per test.  The
    (returncode, combined_output) contract is identical in both modes.
    """
    if os.environ.get("FACIT_INPROCESS") == "1":
        facit_mod = _get_inprocess_module()
        old_argv = sys.argv[:]
        old_stdout = sys.stdout
        old_stderr = sys.stderr
        buf = io.StringIO()
        rc = 0
        try:
            sys.argv = ["facit"] + list(args)
            sys.stdout = buf
            sys.stderr = buf
            facit_mod.main()
            # main() always calls sys.exit(), so this line is a safety net only
            rc = 0
        except SystemExit as e:
            code = e.code
            if code is None:
                rc = 0
            elif isinstance(code, int):
                rc = code
            else:
                # argparse sometimes exits with a string error message
                rc = 1
        except Exception as e:
            buf.write(f"EXCEPTION in facit.main(): {e}\n")
            import traceback
            traceback.print_exc(file=buf)
            rc = 1
        finally:
            sys.argv = old_argv
            sys.stdout = old_stdout
            sys.stderr = old_stderr
        output = buf.getvalue()
        if check and rc != 0:
            raise AssertionError(
                f"Command failed (rc={rc}): {' '.join(str(a) for a in args)}\n"
                f"output: {output}"
            )
        return rc, output
    else:
        cmd = [sys.executable, FACIT_PY] + args
        result = subprocess.run(
            cmd,
            cwd=REPO_ROOT,
            capture_output=capture,
            text=True,
        )
        if check and result.returncode != 0:
            raise AssertionError(
                f"Command failed (rc={result.returncode}): {' '.join(args)}\n"
                f"stdout: {result.stdout}\nstderr: {result.stderr}"
            )
        return result.returncode, (result.stdout or "") + (result.stderr or "")


def read_lock(path):
    with open(path) as f:
        return json.load(f)


# ---------------------------------------------------------------------------
# E1.S1 / E1.S2 / E1.S4.A1 — compile + validate on docs/facit (default root)
# ---------------------------------------------------------------------------

def test_compile_default_root():
    """compile with no --root succeeds and reports nodes."""
    rc, out = run(["compile"])
    assert rc == 0, f"Expected exit 0, got {rc}\n{out}"
    assert "nodes:" in out, f"Expected 'nodes:' in output\n{out}"
    assert "facitHash:" in out, f"Expected 'facitHash:' in output\n{out}"
    print("PASS test_compile_default_root")


def test_validate_default_root():
    """validate with no --root exits zero on the platform facit."""
    rc, out = run(["validate"])
    assert rc == 0, f"Expected exit 0, got {rc}\n{out}"
    assert "VALID" in out, f"Expected 'VALID' in output\n{out}"
    print("PASS test_validate_default_root")


def test_compile_explicit_docs_root():
    """compile --root docs/facit gives same result as no --root."""
    rc1, out1 = run(["compile"])
    rc2, out2 = run(["--root", "docs/facit", "compile"])
    assert rc1 == 0
    assert rc2 == 0
    # Hash and node count must be identical
    assert _extract_hash(out1) == _extract_hash(out2), (
        f"facitHash differs between default and explicit --root docs/facit\n"
        f"default: {out1}\nexplicit: {out2}"
    )
    assert _extract_node_count(out1) == _extract_node_count(out2)
    print("PASS test_compile_explicit_docs_root")


# ---------------------------------------------------------------------------
# E1.S4.A3 — dogfood: compile + validate on the CLI's own facit
# ---------------------------------------------------------------------------

def test_compile_cli_spec():
    """compile --root tools/facit/spec succeeds (E1.S4.A3)."""
    rc, out = run(["--root", "tools/facit/spec", "compile"])
    assert rc == 0, f"Expected exit 0, got {rc}\n{out}"
    assert "nodes:" in out
    assert "facitHash:" in out
    print("PASS test_compile_cli_spec")


def test_validate_cli_spec():
    """validate --root tools/facit/spec succeeds (E1.S4.A3)."""
    rc, out = run(["--root", "tools/facit/spec", "validate"])
    assert rc == 0, f"Expected exit 0, got {rc}\n{out}"
    assert "VALID" in out
    print("PASS test_validate_cli_spec")


# ---------------------------------------------------------------------------
# E1.S6.A3 — lock then diff is clean on tools/facit/spec
# ---------------------------------------------------------------------------

def test_lock_then_diff_clean():
    """After lock, diff reports zero changes (E1.S6.A3)."""
    # Lock (the CLI's own spec lock was already written; we lock again to be sure)
    rc, out = run(["--root", "tools/facit/spec", "lock"])
    assert rc == 0, f"lock failed: {out}"

    rc, out = run(["--root", "tools/facit/spec", "diff"])
    assert rc == 0, f"diff failed: {out}"
    assert "0 node(s) changed since lock" in out, f"Expected clean diff, got:\n{out}"
    print("PASS test_lock_then_diff_clean")


# ---------------------------------------------------------------------------
# E1.S6.A2 — editing one AC makes diff report exactly that one node
# ---------------------------------------------------------------------------

def test_diff_single_ac_change():
    """Editing one AC text in a temp copy makes diff report exactly that one node (E1.S6.A2)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        # Copy tools/facit/spec into tmpdir
        spec_copy = os.path.join(tmpdir, "spec")
        shutil.copytree(CLI_SPEC_DIR, spec_copy)

        # Lock the clean copy
        rc, out = run(["--root", spec_copy, "lock"])
        assert rc == 0, f"lock failed: {out}"

        # Read the facit.json, change exactly one AC text
        facit_path = os.path.join(spec_copy, "facit.json")
        with open(facit_path) as f:
            doc = json.load(f)

        # Find the first AC in the first story of the first epic
        target_ac = doc["epics"][0]["stories"][0]["acceptanceCriteria"][0]
        original_text = target_ac["text"]
        target_qid = f"engine::{target_ac['id']}"
        target_ac["text"] = original_text + " [MODIFIED FOR TEST]"

        with open(facit_path, "w") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)

        # diff must report exactly one changed node
        rc, out = run(["--root", spec_copy, "diff"])
        assert rc == 0, f"diff failed: {out}"
        assert "~ changed" in out, f"Expected changed node, got:\n{out}"

        # Count changed lines
        changed_lines = [l for l in out.splitlines() if "~ changed" in l]
        assert len(changed_lines) == 1, (
            f"Expected exactly 1 changed node, got {len(changed_lines)}:\n{out}"
        )
        assert target_qid in changed_lines[0], (
            f"Expected changed node to be {target_qid}, got: {changed_lines[0]}"
        )

        # No added or removed nodes
        assert "+ added" not in out, f"Unexpected added nodes:\n{out}"
        assert "- removed" not in out, f"Unexpected removed nodes:\n{out}"

        # tmpdir cleanup is automatic
    print("PASS test_diff_single_ac_change")


# ---------------------------------------------------------------------------
# E1.S5.A2 — lock refuses on an invalid facit
# ---------------------------------------------------------------------------

def test_lock_refuses_invalid_facit():
    """lock refuses when the facit.json is schema-invalid (E1.S5.A2)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        # Write a broken facit.json (missing required fields)
        broken = {"schemaVersion": 1}  # missing scope, epics, etc. — schema-invalid
        facit_path = os.path.join(tmpdir, "facit.json")
        with open(facit_path, "w") as f:
            json.dump(broken, f)

        rc, out = run(["--root", tmpdir, "lock"])
        assert rc != 0, f"Expected non-zero exit for invalid facit, got {rc}\n{out}"
        assert "refusing" in out.lower() or "error" in out.lower(), (
            f"Expected refusal message, got:\n{out}"
        )
    print("PASS test_lock_refuses_invalid_facit")


# ---------------------------------------------------------------------------
# E1.S5.A3 — re-lock carries proven status forward only for unchanged nodes
# ---------------------------------------------------------------------------

def test_relock_carries_proven_forward():
    """Re-lock: proven status carries forward for unchanged nodes; changed nodes reset (E1.S5.A3)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_copy = os.path.join(tmpdir, "spec")
        shutil.copytree(CLI_SPEC_DIR, spec_copy)

        # Lock once
        rc, out = run(["--root", spec_copy, "lock"])
        assert rc == 0, f"first lock failed: {out}"

        lock_path = os.path.join(spec_copy, "facit.lock.json")
        lock1 = read_lock(lock_path)

        # Manually mark one node as proven in the lock
        first_qid = sorted(lock1["nodes"].keys())[0]
        lock1["nodes"][first_qid]["status"] = "proven"
        lock1["nodes"][first_qid]["tests"] = ["test_dummy"]
        with open(lock_path, "w") as f:
            json.dump(lock1, f, indent=2)

        # Read the facit, modify a DIFFERENT node's AC text
        facit_path = os.path.join(spec_copy, "facit.json")
        with open(facit_path) as f:
            doc = json.load(f)

        # Find the last AC (likely different from first_qid)
        last_ac = doc["epics"][0]["stories"][-1]["acceptanceCriteria"][-1]
        last_qid = f"engine::{last_ac['id']}"
        assert last_qid != first_qid, "first and last qid must differ for this test"
        last_ac["text"] = last_ac["text"] + " [CHANGED]"
        with open(facit_path, "w") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)

        # Re-lock
        rc, out = run(["--root", spec_copy, "lock"])
        assert rc == 0, f"second lock failed: {out}"

        lock2 = read_lock(lock_path)

        # The unchanged proven node must still be proven
        node1_after = lock2["nodes"][first_qid]
        assert node1_after["status"] == "proven", (
            f"Expected first_qid ({first_qid}) to stay proven, got: {node1_after}"
        )
        assert node1_after["tests"] == ["test_dummy"]

        # The changed node must be reset to unproven
        node_last = lock2["nodes"][last_qid]
        assert node_last["status"] == "unproven", (
            f"Expected last_qid ({last_qid}) to be reset to unproven, got: {node_last}"
        )

    print("PASS test_relock_carries_proven_forward")


# ---------------------------------------------------------------------------
# TASK 1 — E1.S8 prove story must exist in the CLI's own facit
# ---------------------------------------------------------------------------

def test_compiled_cli_facit_has_E1S8A1():
    """The compiled CLI facit must contain node id engine::E1.S8.A1 (E1.S8 prove story)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        compiled_path = os.path.join(tmpdir, "compiled.json")
        rc, out = run(["--root", "tools/facit/spec", "compile", "--out", compiled_path])
        assert rc == 0, f"compile failed: {out}"
        with open(compiled_path) as f:
            compiled = json.load(f)
        assert "engine::E1.S8.A1" in compiled["nodes"], (
            "engine::E1.S8.A1 not found in compiled CLI facit — E1.S8 prove story is missing"
        )
    print("PASS test_compiled_cli_facit_has_E1S8A1")


# ---------------------------------------------------------------------------
# TASK 2 — prove command (TDD)
# ---------------------------------------------------------------------------

def _make_trx(tests_outcomes):
    """Build a minimal .trx XML with the given {testName: outcome} mapping."""
    ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    lines = [
        f'<?xml version="1.0" encoding="utf-8"?>',
        f'<TestRun xmlns="{ns}">',
        f'  <Results>',
    ]
    for i, (name, outcome) in enumerate(tests_outcomes.items()):
        lines.append(
            f'    <UnitTestResult executionId="{i}" testId="{i}" testName="{name}" '
            f'outcome="{outcome}" />'
        )
    lines += ['  </Results>', '</TestRun>']
    return "\n".join(lines)


def _make_minimal_facit(tmpdir, ac_text="Given X, when Y, then Z."):
    """Write a minimal valid facit.json in tmpdir. Returns spec_dir path."""
    spec_dir = os.path.join(tmpdir, "spec")
    os.makedirs(spec_dir)
    doc = {
        "schemaVersion": 1,
        "scope": {"level": "engine"},
        "extends": None,
        "preamble": "test",
        "howToRead": "test",
        "glossary": [{"id": "G.test", "term": "Test", "definition": "A test term."}],
        "epics": [{
            "id": "E1",
            "title": "Test",
            "framing": "test",
            "stories": [{
                "id": "E1.S1",
                "role": "a user",
                "want": "something",
                "soThat": "benefit",
                "acceptanceCriteria": [
                    {"id": "E1.S1.A1", "text": ac_text}
                ]
            }]
        }]
    }
    with open(os.path.join(spec_dir, "facit.json"), "w") as f:
        json.dump(doc, f, indent=2)
    return spec_dir


def _make_impl(tmpdir, spec_dir, test_ids):
    """Write an implementation.json with proof.tests = test_ids for E1.S1.A1."""
    # Lock the spec first
    rc, out = run(["--root", spec_dir, "lock"])
    assert rc == 0, f"lock failed: {out}"

    impl = {
        "schemaVersion": 1,
        "facitVersion": "test",
        "entries": [{
            "acId": "E1.S1.A1",
            "status": "implemented",
            "evidence": [{"kind": "test", "ref": "some.test", "note": "test"}],
            "proof": {"tests": test_ids, "coveredFiles": []}
        }]
    }
    impl_path = os.path.join(tmpdir, "implementation.json")
    with open(impl_path, "w") as f:
        json.dump(impl, f, indent=2)
    return impl_path


def test_prove_marks_ac_proven_when_test_passes():
    """prove marks an AC proven when its bound test is Passed in the TRX (E1.S8.A1)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "MyNS.MyClass.My_passing_test"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        cov_path = _make_covdb(tmpdir, [test_id])
        if cov_path is None:
            print("SKIP test_prove_marks_ac_proven_when_test_passes (coverage not installed)")
            return

        rc, out = run(["--root", spec_dir, "prove",
                        "--impl", impl_path, "--results", trx_path,
                        "--coverage", cov_path])
        assert rc == 0, f"prove failed (rc={rc}):\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None, "engine::E1.S1.A1 not in lock"
        assert node["status"] == "proven", f"Expected proven, got: {node['status']}"
        assert test_id in node["tests"], f"Expected test id in lock tests: {node['tests']}"
        assert node.get("coveredFiles"), f"Expected non-empty coveredFiles: {node}"
    print("PASS test_prove_marks_ac_proven_when_test_passes")


def test_prove_refuses_when_test_failed():
    """prove refuses (exit non-zero) when the bound test is Failed in the TRX (E1.S8.A2)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "MyNS.MyClass.My_failing_test"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Failed"}))

        rc, out = run(["--root", spec_dir, "prove",
                        "--impl", impl_path, "--results", trx_path])
        assert rc != 0, f"Expected non-zero exit for failed test, got rc={rc}\n{out}"

        # Lock must NOT have proven status
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", (
            f"Lock must not show proven on failed test: {node}"
        )
    print("PASS test_prove_refuses_when_test_failed")


def test_prove_refuses_proven_without_binding():
    """--verify errors on a proven lock node with empty tests (E1.S8.A3)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        # Lock the spec
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        # Manually mark a node proven with NO tests in the lock
        lock_path = os.path.join(spec_dir, "facit.lock.json")
        lock = read_lock(lock_path)
        lock["nodes"]["engine::E1.S1.A1"]["status"] = "proven"
        lock["nodes"]["engine::E1.S1.A1"]["tests"] = []
        with open(lock_path, "w") as f:
            json.dump(lock, f, indent=2)

        rc, out = run(["--root", spec_dir, "prove", "--verify"])
        assert rc != 0, f"Expected non-zero from --verify with no binding, got rc={rc}\n{out}"
        assert "proven-without-binding" in out.lower() or "no test" in out.lower() or "binding" in out.lower(), (
            f"Expected binding error in output:\n{out}"
        )
    print("PASS test_prove_refuses_proven_without_binding")


def test_prove_refuses_when_node_diff_dirty():
    """prove refuses when the AC's facit node changed since the lock (E1.S8.A4)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "MyNS.MyClass.My_passing_test"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        # Now edit the AC text AFTER locking (making it diff-dirty)
        facit_path = os.path.join(spec_dir, "facit.json")
        with open(facit_path) as f:
            doc = json.load(f)
        doc["epics"][0]["stories"][0]["acceptanceCriteria"][0]["text"] = (
            "Given X, when Y, then Z. [MODIFIED AFTER LOCK]"
        )
        with open(facit_path, "w") as f:
            json.dump(doc, f, indent=2)

        rc, out = run(["--root", spec_dir, "prove",
                        "--impl", impl_path, "--results", trx_path])
        assert rc != 0, f"Expected non-zero when node is diff-dirty, got rc={rc}\n{out}"
        assert "diff" in out.lower() or "hash" in out.lower() or "dirty" in out.lower() or "changed" in out.lower(), (
            f"Expected diff-dirty message:\n{out}"
        )
    print("PASS test_prove_refuses_when_node_diff_dirty")


# ---------------------------------------------------------------------------
# TASK 3 — implementation.json E1.S1.A1 must carry a non-empty proof.tests
# ---------------------------------------------------------------------------

def test_impl_E1S1A1_has_proof_tests():
    """The app implementation.json must carry at least one non-empty proof.tests binding."""
    impl_path = os.path.join(DOCS_FACIT_DIR, "app", "implementation.json")
    assert os.path.exists(impl_path), f"implementation.json not found at {impl_path}"
    with open(impl_path) as f:
        impl = json.load(f)
    bound = [e for e in impl["entries"] if e.get("proof", {}).get("tests")]
    assert len(bound) > 0, "no implementation entry carries a non-empty proof.tests binding"
    print("PASS test_impl_E1S1A1_has_proof_tests")


# ---------------------------------------------------------------------------
# Safety check: docs/facit/facit.lock.json must not be mutated
# ---------------------------------------------------------------------------

def test_docs_lock_untouched():
    """The docs/facit/facit.lock.json must not be touched by any --root tools/facit/spec operation."""
    if not os.path.exists(DOCS_LOCK):
        print("SKIP test_docs_lock_untouched (no docs lock exists)")
        return

    with open(DOCS_LOCK) as f:
        before = f.read()

    # Run several dogfood operations
    run(["--root", "tools/facit/spec", "compile"])
    run(["--root", "tools/facit/spec", "validate"])
    run(["--root", "tools/facit/spec", "diff"])

    with open(DOCS_LOCK) as f:
        after = f.read()

    assert before == after, "docs/facit/facit.lock.json was mutated!"
    print("PASS test_docs_lock_untouched")


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _extract_hash(output):
    for line in output.splitlines():
        if "facitHash:" in line:
            return line.split("facitHash:")[-1].strip()
    return None


def _extract_node_count(output):
    for line in output.splitlines():
        if line.startswith("nodes:"):
            return line.split()[1]
    return None


# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------

def _make_trx_with_defs(entries):
    """Build a .trx with a TestDefinitions section so _parse_trx records BOTH the
    display name AND the className.methodName (FQN) for each test id.

    entries: list of dicts {id, displayName, className, methodName, outcome}.
    """
    ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<TestRun xmlns="{ns}">',
        '  <TestDefinitions>',
    ]
    for e in entries:
        lines.append(
            f'    <UnitTest name="{e["displayName"]}" id="{e["id"]}">'
            f'<TestMethod className="{e["className"]}" name="{e["methodName"]}" /></UnitTest>'
        )
    lines.append('  </TestDefinitions>')
    lines.append('  <Results>')
    for e in entries:
        lines.append(
            f'    <UnitTestResult executionId="{e["id"]}" testId="{e["id"]}" '
            f'testName="{e["displayName"]}" outcome="{e["outcome"]}" />'
        )
    lines += ['  </Results>', '</TestRun>']
    return "\n".join(lines)


def _make_facit_two_acs(tmpdir):
    """Write a minimal valid facit.json with two ACs (E1.S1.A1, E1.S1.A2). Returns spec_dir."""
    spec_dir = os.path.join(tmpdir, "spec")
    os.makedirs(spec_dir)
    doc = {
        "schemaVersion": 1,
        "scope": {"level": "engine"},
        "extends": None,
        "preamble": "test",
        "howToRead": "test",
        "glossary": [{"id": "G.test", "term": "Test", "definition": "A test term."}],
        "epics": [{
            "id": "E1", "title": "Test", "framing": "test",
            "stories": [{
                "id": "E1.S1", "role": "a user", "want": "something", "soThat": "benefit",
                "acceptanceCriteria": [
                    {"id": "E1.S1.A1", "text": "Given A, when B, then C."},
                    {"id": "E1.S1.A2", "text": "Given D, when E, then F."},
                ]
            }]
        }]
    }
    with open(os.path.join(spec_dir, "facit.json"), "w") as f:
        json.dump(doc, f, indent=2)
    return spec_dir


def _write_impl(path, ac_id, test_ids):
    impl = {
        "schemaVersion": 1, "facitVersion": "test",
        "entries": [{
            "acId": ac_id, "status": "implemented",
            "evidence": [{"kind": "test", "ref": "x.test", "note": "t"}],
            "proof": {"tests": test_ids, "coveredFiles": []},
        }]
    }
    with open(path, "w") as f:
        json.dump(impl, f, indent=2)
    return path


def test_prove_spans_multiple_impls_and_results_files():
    """E1.S8.A5 — repeated --impl + --results: a binding in any map is satisfied by a
    test recorded in any results file (proofs spanning test projects)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_two_acs(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        t1 = "ProjA.ClsA.Test_one"
        t2 = "ProjB.ClsB.Test_two"
        impl1 = _write_impl(os.path.join(tmpdir, "impl1.json"), "E1.S1.A1", [t1])
        impl2 = _write_impl(os.path.join(tmpdir, "impl2.json"), "E1.S1.A2", [t2])

        # Each test lives in a DIFFERENT results file — A1's test only in trx1, A2's only in trx2.
        trx1 = os.path.join(tmpdir, "projA.trx")
        trx2 = os.path.join(tmpdir, "projB.trx")
        with open(trx1, "w") as f:
            f.write(_make_trx({t1: "Passed"}))
        with open(trx2, "w") as f:
            f.write(_make_trx({t2: "Passed"}))

        # E1.S9.A5 strict: coverage required.  Both test contexts cover the same helper file.
        cov_path = _make_covdb(tmpdir, [t1, t2])
        if cov_path is None:
            print("SKIP test_prove_spans_multiple_impls_and_results_files (coverage not installed)")
            return

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl1, "--impl", impl2,
                       "--results", trx1, "--results", trx2,
                       "--coverage", cov_path])
        assert rc == 0, f"prove failed (rc={rc}):\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        for ac in ("engine::E1.S1.A1", "engine::E1.S1.A2"):
            node = lock["nodes"].get(ac)
            assert node is not None and node["status"] == "proven", (
                f"{ac} must be proven across merged impls+results, got: {node}")
            assert node.get("coveredFiles"), f"{ac} must have coveredFiles: {node}"
    print("PASS test_prove_spans_multiple_impls_and_results_files")


def test_prove_collapses_displayname_and_fqn_of_same_test():
    """E1.S8.A6 — a test recorded under BOTH its display name AND its FQN with the same
    outcome is treated as ONE test (not an ambiguous match), so the AC proves."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        # The binding is a class.method form that suffix-matches BOTH the display-name
        # key ("My_test") and the FQN key ("MyNS.MyClass.My_test") of the SAME test id.
        binding = "MyClass.My_test"
        impl_path = _make_impl(tmpdir, spec_dir, [binding])

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx_with_defs([{
                "id": "11111111-1111-1111-1111-111111111111",
                "displayName": "My_test",
                "className": "MyNS.MyClass",
                "methodName": "My_test",
                "outcome": "Passed",
            }]))

        # E1.S9.A5 strict: must supply coverage with the binding as context.
        cov_path = _make_covdb(tmpdir, [binding])
        if cov_path is None:
            print("SKIP test_prove_collapses_displayname_and_fqn_of_same_test (coverage not installed)")
            return

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path])
        assert rc == 0, f"dup display+FQN of one test must collapse, not be ambiguous:\n{out}"
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None and node["status"] == "proven", f"expected proven, got: {node}"
        assert node.get("coveredFiles"), f"expected non-empty coveredFiles: {node}"
    print("PASS test_prove_collapses_displayname_and_fqn_of_same_test")


def test_prove_refuses_genuine_ambiguous_match():
    """E1.S8.A7 — when a binding matches two genuinely DIFFERENT tests with DIFFERING
    outcomes, prove reports an ambiguous match and refuses that AC (fail closed)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        binding = "Cls.M"  # suffix-matches both NsA.Cls.M and NsB.Cls.M below
        impl_path = _make_impl(tmpdir, spec_dir, [binding])

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            # Two DIFFERENT tests, differing outcomes — a genuine ambiguity, not a dup.
            f.write(_make_trx({"NsA.Cls.M": "Passed", "NsB.Cls.M": "Failed"}))

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path])
        assert rc != 0, f"genuine ambiguous match (differing outcomes) must refuse:\n{out}"
        assert "mbiguous" in out, f"expected an ambiguity diagnostic, got:\n{out}"
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", f"must not prove on ambiguity: {node}"
    print("PASS test_prove_refuses_genuine_ambiguous_match")


# ---------------------------------------------------------------------------
# E1.S9 — code-drift conformance (TDD: write tests first, watch them fail)
# ---------------------------------------------------------------------------

def _import_facit_module():
    """Import tools/facit/facit.py as a module so we can call internal functions."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("facit_module", FACIT_PY)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _import_selfproof_module():
    """Import tools/facit/selfproof.py as a module so we can call _verdict directly."""
    import importlib.util
    selfproof_py = os.path.join(REPO_ROOT, "tools", "facit", "selfproof.py")
    sp = importlib.util.spec_from_file_location("selfproof_module", selfproof_py)
    mod = importlib.util.module_from_spec(sp)
    sp.loader.exec_module(mod)
    return mod


def _make_covdb(tmpdir, test_ids, filename="impl_cov.py"):
    """coverage.py DB where each test_id context covers a real file under tmpdir.

    Returns the path to the .coverage file, or None if coverage is not installed.
    The covered file is created on disk at tmpdir/filename so _compute_covered_files
    can hash it (it must exist).  Each test_id becomes a coverage context that
    covers line 1 of that file, so per-test lookup in _compute_covered_files
    returns a non-empty set for every test_id.
    """
    try:
        import coverage as coverage_mod
    except ImportError:
        return None
    src = os.path.join(tmpdir, filename)
    if not os.path.exists(src):
        with open(src, "w") as f:
            f.write("def covered():\n    return 1\n")
    cov_path = os.path.join(tmpdir, ".coverage")
    cd = coverage_mod.CoverageData(basename=cov_path)
    for tid in test_ids:
        cd.set_context(tid)
        cd.add_lines({src: {1}})
    cd.write()
    return cov_path


def test_parse_coverage_perTest_from_coveragepy():
    """_parse_coverage returns correct perTest map from a coverage.py sqlite DB (E1.S9)."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_parse_coverage_perTest_from_coveragepy (coverage not installed)")
        return

    facit_mod = _import_facit_module()

    with tempfile.TemporaryDirectory() as tmp:
        impl_a = os.path.join(tmp, "impl_a.py")
        impl_b = os.path.join(tmp, "impl_b.py")
        with open(impl_a, "w") as f:
            f.write("x = 1\ny = 2\nz = 3\n")
        with open(impl_b, "w") as f:
            f.write("a = 1\n")

        cov_path = os.path.join(tmp, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context("test_alpha")
        cd.add_lines({impl_a: {1, 2, 3}})
        cd.set_context("test_beta")
        cd.add_lines({impl_b: {1}})
        cd.write()

        result = facit_mod._parse_coverage(cov_path)

        assert result["perTest"] is not None, "expected perTest to be a dict, got None"
        assert "test_alpha" in result["perTest"], (
            f"expected 'test_alpha' in perTest keys: {list(result['perTest'].keys())}")
        assert "test_beta" in result["perTest"], (
            f"expected 'test_beta' in perTest keys: {list(result['perTest'].keys())}")
        assert impl_a in result["perTest"]["test_alpha"], (
            f"expected impl_a in test_alpha files: {result['perTest']['test_alpha']}")
        assert impl_b in result["perTest"]["test_beta"], (
            f"expected impl_b in test_beta files: {result['perTest']['test_beta']}")
        assert impl_a in result["files"], f"impl_a must be in aggregate files: {result['files']}"
        assert impl_b in result["files"], f"impl_b must be in aggregate files: {result['files']}"

    print("PASS test_parse_coverage_perTest_from_coveragepy")


def test_prove_records_coveredFiles_from_coverage():
    """prove --coverage records coveredFiles on the lock node (E1.S9.A1)."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_prove_records_coveredFiles_from_coverage (coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "test_alpha"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        # Create an implementation file that coverage will cover
        impl_a = os.path.join(tmpdir, "impl_a.py")
        with open(impl_a, "w") as f:
            f.write("def foo(): pass\n")

        # Build coverage DB: context "test_alpha" covers impl_a.py
        cov_path = os.path.join(tmpdir, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context(test_id)
        cd.add_lines({impl_a: {1}})
        cd.write()

        # TRX with test_alpha → Passed
        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        # prove with --coverage; --src-root defaults to tmpdir (parent of spec_dir)
        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path])
        assert rc == 0, f"prove with --coverage failed (rc={rc}):\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None, "engine::E1.S1.A1 missing from lock"
        assert node["status"] == "proven", f"expected proven, got: {node['status']}"

        covered = node.get("coveredFiles", [])
        assert len(covered) > 0, (
            f"expected coveredFiles to be non-empty after prove with --coverage: {node}")

        paths = [cf["path"] for cf in covered]
        assert any("impl_a.py" in p for p in paths), (
            f"expected impl_a.py somewhere in coveredFiles paths: {paths}")

        for cf in covered:
            assert "path" in cf and "hash" in cf, f"coveredFiles entry missing path/hash: {cf}"
            assert len(cf["hash"]) == 16, f"hash must be 16 hex chars: {cf['hash']}"

    print("PASS test_prove_records_coveredFiles_from_coverage")


def test_conform_reports_conformant_then_drifted():
    """conform exits 0 when all covered files match, non-zero when a file is modified (E1.S9.A2)."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_conform_reports_conformant_then_drifted (coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "test_alpha"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        impl_a = os.path.join(tmpdir, "impl_a.py")
        with open(impl_a, "w") as f:
            f.write("def foo(): pass\n")

        cov_path = os.path.join(tmpdir, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context(test_id)
        cd.add_lines({impl_a: {1}})
        cd.write()

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path])
        assert rc == 0, f"prove with coverage failed:\n{out}"

        # First conform → all conformant (no drift yet)
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc == 0, f"conform should exit 0 when all conformant, got rc={rc}:\n{out}"
        assert "0 drifted" in out, f"expected '0 drifted' in output:\n{out}"

        # Modify impl_a.py → triggers drift
        with open(impl_a, "w") as f:
            f.write("def foo(): return 42  # changed\n")

        # Second conform → drifted, exit non-zero
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc != 0, f"conform should exit non-zero when drifted, got rc={rc}:\n{out}"
        assert "drifted" in out.lower(), f"expected 'drifted' in output:\n{out}"
        assert any("impl_a" in line for line in out.splitlines()), (
            f"expected impl_a.py mentioned in drifted output:\n{out}")

    print("PASS test_conform_reports_conformant_then_drifted")


def test_conform_flags_unverifiable_when_no_coveredfiles():
    """conform exits NON-ZERO and reports UNVERIFIABLE for proven nodes with empty coveredFiles
    (E1.S9.A3 strict).

    Since prove now refuses to create a proven node without coverage (E1.S9.A5), we
    hand-craft the lock to inject such a node, then verify that conform catches it.
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        # Lock the spec to get a fresh lock with the correct hash for E1.S1.A1.
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        lock_path = os.path.join(spec_dir, "facit.lock.json")
        lock = read_lock(lock_path)
        # Inject a proven node with an empty coveredFiles — the state prove can no longer
        # create (E1.S9.A5), but that conform must still detect and reject (E1.S9.A3 strict).
        lock["nodes"]["engine::E1.S1.A1"]["status"] = "proven"
        lock["nodes"]["engine::E1.S1.A1"]["tests"] = ["some_test"]
        lock["nodes"]["engine::E1.S1.A1"]["coveredFiles"] = []
        with open(lock_path, "w") as f:
            json.dump(lock, f, indent=2)

        # conform: proven node with empty coveredFiles → UNVERIFIABLE → non-zero exit (strict).
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc != 0, (
            f"conform must exit non-zero when a proven node has no coveredFiles:\n{out}")
        assert "unverifiable" in out.lower(), f"expected 'unverifiable' in output:\n{out}"

    print("PASS test_conform_flags_unverifiable_when_no_coveredfiles")


# ---------------------------------------------------------------------------
# E1.S10 — genuine-proof guard (TDD: write tests first, watch them fail)
# ---------------------------------------------------------------------------

def _make_facit_n_acs(tmpdir, n):
    """Write a minimal valid facit.json with n ACs in E1.S1. Returns spec_dir."""
    spec_dir = os.path.join(tmpdir, "spec")
    os.makedirs(spec_dir)
    acs = [{"id": f"E1.S1.A{i}", "text": f"Given {i}, when {i}, then {i}."} for i in range(1, n + 1)]
    doc = {
        "schemaVersion": 1,
        "scope": {"level": "engine"},
        "extends": None,
        "preamble": "test",
        "howToRead": "test",
        "glossary": [{"id": "G.test", "term": "Test", "definition": "A test term."}],
        "epics": [{
            "id": "E1", "title": "Test", "framing": "test",
            "stories": [{
                "id": "E1.S1", "role": "a user", "want": "something", "soThat": "benefit",
                "acceptanceCriteria": acs,
            }]
        }]
    }
    with open(os.path.join(spec_dir, "facit.json"), "w") as f:
        json.dump(doc, f, indent=2)
    return spec_dir


def test_prove_rejects_binding_whose_test_misses_impl_coverage():
    """E1.S10.A1: prove refuses an AC whose test does not cover its declared impl file."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_prove_rejects_binding_whose_test_misses_impl_coverage (coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        # Create impl files on disk (coverage.py needs real paths)
        impl_a = os.path.join(tmpdir, "impl_a.py")
        impl_b = os.path.join(tmpdir, "impl_b.py")
        with open(impl_a, "w") as f:
            f.write("x = 1\n")
        with open(impl_b, "w") as f:
            f.write("y = 2\n")

        test_id = "test_alpha"

        # Lock the spec first
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        # impl entry: code evidence declares impl_a.py; proof test is "test_alpha"
        impl = {
            "schemaVersion": 1,
            "facitVersion": "test",
            "entries": [{
                "acId": "E1.S1.A1",
                "status": "implemented",
                "evidence": [
                    {"kind": "code", "ref": impl_a},   # declares impl_a.py
                    {"kind": "test", "ref": test_id, "note": "t"},
                ],
                "proof": {"tests": [test_id], "coveredFiles": []},
            }]
        }
        impl_path = os.path.join(tmpdir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # Coverage: "test_alpha" covers ONLY impl_b.py — NOT impl_a.py
        cov_path = os.path.join(tmpdir, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context(test_id)
        cd.add_lines({impl_b: {1}})
        cd.write()

        # TRX: test_alpha passes
        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        # prove: test passes TRX but misses impl → must be refused
        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path, "--src-root", tmpdir])
        assert rc != 0, f"Expected non-zero exit for COVERAGE-MISS, got rc={rc}:\n{out}"
        assert "COVERAGE-MISS" in out, f"Expected COVERAGE-MISS in output:\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", (
            f"Must NOT be proven when test misses declared impl: {node}")
    print("PASS test_prove_rejects_binding_whose_test_misses_impl_coverage")


def test_prove_accepts_when_coverage_intersects_impl():
    """E1.S10.A1: prove accepts an AC whose test covers its declared impl file."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_prove_accepts_when_coverage_intersects_impl (coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        impl_a = os.path.join(tmpdir, "impl_a.py")
        with open(impl_a, "w") as f:
            f.write("x = 1\n")

        test_id = "test_alpha"

        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        impl = {
            "schemaVersion": 1,
            "facitVersion": "test",
            "entries": [{
                "acId": "E1.S1.A1",
                "status": "implemented",
                "evidence": [
                    {"kind": "code", "ref": impl_a},   # declares impl_a.py
                ],
                "proof": {"tests": [test_id], "coveredFiles": []},
            }]
        }
        impl_path = os.path.join(tmpdir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # Coverage: "test_alpha" covers impl_a.py — intersection is non-empty
        cov_path = os.path.join(tmpdir, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context(test_id)
        cd.add_lines({impl_a: {1}})
        cd.write()

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path, "--src-root", tmpdir])
        assert rc == 0, f"Expected exit 0 when coverage intersects impl, got rc={rc}:\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None and node["status"] == "proven", (
            f"Expected proven when coverage intersects impl, got: {node}")
    print("PASS test_prove_accepts_when_coverage_intersects_impl")


def test_prove_skips_coverage_check_without_perTest():
    """E1.S10.A1: prove skips the per-test intersection check when coverage is aggregate-only.

    With aggregate-only (cobertura) coverage, perTest is None, so the COVERAGE-MISS
    intersection check is not run.  To satisfy E1.S9.A5 (strict), coveredFiles must
    still be non-empty — we achieve this by including impl_a.py IN the cobertura XML
    (with hits=1 and a <source> element so it resolves to an absolute path).  The key
    assertion is that COVERAGE-MISS is never emitted, even though the only reason
    impl_a is "covered" is by aggregate data (no per-test attribution).
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        impl_a = os.path.join(tmpdir, "impl_a.py")
        with open(impl_a, "w") as f:
            f.write("x = 1\n")

        test_id = "test_alpha"

        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        impl = {
            "schemaVersion": 1,
            "facitVersion": "test",
            "entries": [{
                "acId": "E1.S1.A1",
                "status": "implemented",
                "evidence": [
                    {"kind": "code", "ref": impl_a},   # declares impl_a.py
                ],
                "proof": {"tests": [test_id], "coveredFiles": []},
            }]
        }
        impl_path = os.path.join(tmpdir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # Cobertura XML — perTest will be None (aggregate only).
        # We include BOTH some_other.py and impl_a.py so that _compute_covered_files
        # (aggregate path) intersects impl_a.py and produces a non-empty coveredFiles.
        # The COVERAGE-MISS check is still skipped because cov_per_test is None.
        cov_xml = (
            '<?xml version="1.0" ?>\n'
            '<coverage version="5.5" timestamp="0" lines-valid="2" lines-covered="2"'
            ' branch-rate="0" branches-covered="0" branches-valid="0" complexity="0">\n'
            '  <sources><source>' + tmpdir + '</source></sources>\n'
            '  <packages>\n'
            '    <package name="." line-rate="1.0" branch-rate="0" complexity="0">\n'
            '      <classes>\n'
            '        <class name="other.py" filename="some_other.py"'
            ' line-rate="1.0" branch-rate="0" complexity="0">\n'
            '          <lines><line number="1" hits="1"/></lines>\n'
            '        </class>\n'
            '        <class name="impl_a.py" filename="impl_a.py"'
            ' line-rate="1.0" branch-rate="0" complexity="0">\n'
            '          <lines><line number="1" hits="1"/></lines>\n'
            '        </class>\n'
            '      </classes>\n'
            '    </package>\n'
            '  </packages>\n'
            '</coverage>\n'
        )
        cov_path = os.path.join(tmpdir, "coverage.xml")
        with open(cov_path, "w") as f:
            f.write(cov_xml)

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))

        # prove with aggregate-only coverage → intersection check SKIPPED → no COVERAGE-MISS
        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path, "--src-root", tmpdir])
        assert rc == 0, (
            f"Expected exit 0 (check skipped for aggregate-only coverage), got rc={rc}:\n{out}")
        assert "COVERAGE-MISS" not in out, (
            f"COVERAGE-MISS must NOT appear for aggregate-only coverage:\n{out}")

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None and node["status"] == "proven", (
            f"Expected proven (check skipped for aggregate coverage), got: {node}")
        assert node.get("coveredFiles"), (
            f"Expected non-empty coveredFiles (aggregate path populated it): {node}")
    print("PASS test_prove_skips_coverage_check_without_perTest")


def test_prove_warns_on_high_test_share():
    """E1.S10.A2: prove --max-test-share warns when one test is bound to too many ACs."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_n_acs(tmpdir, 6)

        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        shared_test = "MyNS.SharedTest"

        # All 6 ACs bound to the same test id
        entries = []
        for i in range(1, 7):
            entries.append({
                "acId": f"E1.S1.A{i}",
                "status": "implemented",
                "evidence": [{"kind": "test", "ref": shared_test, "note": "t"}],
                "proof": {"tests": [shared_test], "coveredFiles": []},
            })
        impl = {"schemaVersion": 1, "facitVersion": "test", "entries": entries}
        impl_path = os.path.join(tmpdir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({shared_test: "Passed"}))

        # E1.S9.A5 strict: supply coverage so each AC can be proven.
        cov_path = _make_covdb(tmpdir, [shared_test])
        if cov_path is None:
            print("SKIP test_prove_warns_on_high_test_share (coverage not installed)")
            return

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path,
                       "--max-test-share", "5"])
        # Warnings only — must not change exit code
        assert rc == 0, f"Expected exit 0 (warnings are non-fatal), got rc={rc}:\n{out}"
        assert "INFLATION-WARN" in out, f"Expected INFLATION-WARN in output:\n{out}"
        assert "6" in out, f"Expected count '6' in INFLATION-WARN line:\n{out}"

        # All 6 ACs must still be proven
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        proven = [qid for qid, n in lock["nodes"].items() if n.get("status") == "proven"]
        assert len(proven) == 6, f"Expected 6 ACs proven, got {len(proven)}: {proven}"
    print("PASS test_prove_warns_on_high_test_share")


# ---------------------------------------------------------------------------
# E1.S11.A1 — junit ingestion + in-process harness (TDD: written first)
# ---------------------------------------------------------------------------

def _make_junit(tests_outcomes):
    """Build a minimal JUnit XML (pytest --junitxml style).

    tests_outcomes: dict of {name: (classname, outcome)} or {name: outcome}.
    outcome is "Passed", "Failed", or "Skipped".
    Classname defaults to "test_facit" when a bare outcome string is given.
    """
    entries = []
    for name, val in tests_outcomes.items():
        if isinstance(val, tuple):
            classname, outcome = val
        else:
            classname, outcome = "test_facit", val
        entries.append((classname, name, outcome))

    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<testsuites>',
        f'  <testsuite name="pytest" tests="{len(entries)}" errors="0" failures="0" skipped="0">',
    ]
    for classname, name, outcome in entries:
        if outcome == "Failed":
            lines.append(f'    <testcase classname="{classname}" name="{name}" time="0.001">')
            lines.append(f'      <failure message="AssertionError">test failed</failure>')
            lines.append(f'    </testcase>')
        elif outcome == "Skipped":
            lines.append(f'    <testcase classname="{classname}" name="{name}" time="0.001">')
            lines.append(f'      <skipped message="skipped"/>')
            lines.append(f'    </testcase>')
        else:  # Passed
            lines.append(f'    <testcase classname="{classname}" name="{name}" time="0.001"/>')
    lines += ['  </testsuite>', '</testsuites>']
    return "\n".join(lines)


def test_parse_junit_outcomes():
    """_parse_junit returns Passed/Failed under bare name AND classname.name keys (E1.S11.A1)."""
    facit_mod = _import_facit_module()

    with tempfile.TemporaryDirectory() as tmpdir:
        junit_xml = _make_junit({
            "test_passing_one": ("test_facit", "Passed"),
            "test_failing_one": ("test_facit", "Failed"),
        })
        junit_path = os.path.join(tmpdir, "results.xml")
        with open(junit_path, "w") as f:
            f.write(junit_xml)

        results = facit_mod._parse_junit(junit_path)

        # Bare name keys
        assert results.get("test_passing_one") == "Passed", (
            f"Expected Passed for bare name 'test_passing_one', got: {results.get('test_passing_one')}\n"
            f"All keys: {list(results.keys())}")
        assert results.get("test_failing_one") == "Failed", (
            f"Expected Failed for bare name 'test_failing_one', got: {results.get('test_failing_one')}")

        # classname.name keys
        assert results.get("test_facit.test_passing_one") == "Passed", (
            f"Expected Passed for 'test_facit.test_passing_one', got: {results.get('test_facit.test_passing_one')}")
        assert results.get("test_facit.test_failing_one") == "Failed", (
            f"Expected Failed for 'test_facit.test_failing_one', got: {results.get('test_facit.test_failing_one')}")

    print("PASS test_parse_junit_outcomes")


def test_prove_accepts_junit_results():
    """prove accepts JUnit XML (pytest --junitxml) in addition to VSTest TRX (E1.S11.A1)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "test_x"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])

        # JUnit XML: test_x passing, keyed by bare name
        junit_path = os.path.join(tmpdir, "results.xml")
        with open(junit_path, "w") as f:
            f.write(_make_junit({"test_x": ("test_module", "Passed")}))

        # E1.S9.A5 strict: supply coverage so the AC can be proven.
        cov_path = _make_covdb(tmpdir, [test_id])
        if cov_path is None:
            print("SKIP test_prove_accepts_junit_results (coverage not installed)")
            return

        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", junit_path,
                       "--coverage", cov_path])
        assert rc == 0, f"prove with junit failed (rc={rc}):\n{out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        node = lock["nodes"].get("engine::E1.S1.A1")
        assert node is not None, "engine::E1.S1.A1 not in lock"
        assert node["status"] == "proven", f"Expected proven, got: {node['status']}"
        assert test_id in node["tests"], f"Expected test id in lock tests: {node['tests']}"
        assert node.get("coveredFiles"), f"Expected non-empty coveredFiles: {node}"
    print("PASS test_prove_accepts_junit_results")


def test_inprocess_run_matches_subprocess():
    """In-process run() gives same return code and key output as subprocess (E1.S11.A1)."""
    args = ["--root", "tools/facit/spec", "validate"]

    # Always run subprocess regardless of FACIT_INPROCESS env var
    cmd = [sys.executable, FACIT_PY] + args
    result = subprocess.run(cmd, cwd=REPO_ROOT, capture_output=True, text=True)
    sub_rc = result.returncode
    sub_out = (result.stdout or "") + (result.stderr or "")

    # Run in-process explicitly (set env var, then restore)
    old = os.environ.get("FACIT_INPROCESS")
    os.environ["FACIT_INPROCESS"] = "1"
    try:
        inproc_rc, inproc_out = run(args)
    finally:
        if old is None:
            os.environ.pop("FACIT_INPROCESS", None)
        else:
            os.environ["FACIT_INPROCESS"] = old

    assert sub_rc == inproc_rc, (
        f"Return codes differ: subprocess={sub_rc}, in-process={inproc_rc}\n"
        f"subprocess: {sub_out}\nin-process: {inproc_out}"
    )
    assert "VALID" in inproc_out, f"Expected 'VALID' in in-process output:\n{inproc_out}"
    assert "VALID" in sub_out, f"Expected 'VALID' in subprocess output:\n{sub_out}"
    print("PASS test_inprocess_run_matches_subprocess")


# ---------------------------------------------------------------------------
# NEW PINNING TESTS — compile/validate/integrity/lock/diff/status/gap/conform gaps
# ---------------------------------------------------------------------------

# E1.S1.A2 — compile reports node count + facitHash; hash is stable across runs
def test_compile_reports_counts_and_stable_hash():
    """E1.S1.A2: compile reports node count + facitHash; hash is deterministic."""
    rc1, out1 = run(["--root", "tools/facit/spec", "compile"])
    assert rc1 == 0, f"first compile failed: {out1}"
    assert "nodes:" in out1, f"Expected 'nodes:' in output:\n{out1}"
    assert "facitHash:" in out1, f"Expected 'facitHash:' in output:\n{out1}"

    rc2, out2 = run(["--root", "tools/facit/spec", "compile"])
    assert rc2 == 0, f"second compile failed: {out2}"

    hash1 = _extract_hash(out1)
    hash2 = _extract_hash(out2)
    assert hash1 is not None, f"could not extract facitHash from:\n{out1}"
    assert hash2 is not None, f"could not extract facitHash from:\n{out2}"
    assert hash1 == hash2, f"facitHash unstable between two runs: {hash1!r} vs {hash2!r}"

    # Globally-qualified ids: lock spec into a temp copy, confirm node keys contain "::"
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_copy = os.path.join(tmpdir, "spec")
        shutil.copytree(CLI_SPEC_DIR, spec_copy)
        rc, out = run(["--root", spec_copy, "lock"])
        assert rc == 0, f"lock failed: {out}"
        lock = read_lock(os.path.join(spec_copy, "facit.lock.json"))
        keys = list(lock["nodes"].keys())
        assert any("::" in k for k in keys), (
            f"No globally-qualified id found in lock keys: {keys[:5]}")
        assert any(k.startswith("engine::") for k in keys), (
            f"Expected 'engine::' prefix in lock keys: {keys[:5]}")
    print("PASS test_compile_reports_counts_and_stable_hash")


# E1.S1.A3 — compile exits 0 on valid facit, non-zero on schema/integrity error
def test_compile_exit_codes_valid_zero_invalid_nonzero():
    """E1.S1.A3: compile exits 0 for a valid facit, non-zero for an integrity-broken one."""
    with tempfile.TemporaryDirectory() as tmpdir:
        # Valid facit
        spec_dir = _make_minimal_facit(tmpdir)
        rc, out = run(["--root", spec_dir, "compile"])
        assert rc == 0, f"Expected rc 0 for valid facit, got {rc}:\n{out}"

        # Integrity-broken: duplicate AC id (no jsonschema required — caught by integrity check)
        bad_dir = os.path.join(tmpdir, "bad")
        os.makedirs(bad_dir)
        bad_doc = {
            "schemaVersion": 1,
            "scope": {"level": "engine"},
            "extends": None,
            "preamble": "x",
            "howToRead": "x",
            "glossary": [{"id": "G.x", "term": "X", "definition": "X."}],
            "epics": [{
                "id": "E1", "title": "T", "framing": "F",
                "stories": [{
                    "id": "E1.S1", "role": "r", "want": "w", "soThat": "s",
                    "acceptanceCriteria": [
                        {"id": "E1.S1.A1", "text": "one"},
                        {"id": "E1.S1.A1", "text": "duplicate"},  # same id
                    ]
                }]
            }]
        }
        with open(os.path.join(bad_dir, "facit.json"), "w") as f:
            json.dump(bad_doc, f)
        rc, out = run(["--root", bad_dir, "compile"])
        assert rc != 0, f"Expected non-zero for integrity-broken facit, got {rc}:\n{out}"
    print("PASS test_compile_exit_codes_valid_zero_invalid_nonzero")


# E1.S2.A2 — validate prints errors and exits non-zero on invalid facit
def test_validate_invalid_prints_errors_nonzero():
    """E1.S2.A2: validate on an invalid facit prints errors and exits non-zero."""
    with tempfile.TemporaryDirectory() as tmpdir:
        bad_dir = os.path.join(tmpdir, "bad")
        os.makedirs(bad_dir)
        # Duplicate AC id — integrity error, no jsonschema dependency
        bad_doc = {
            "schemaVersion": 1,
            "scope": {"level": "engine"},
            "extends": None,
            "preamble": "x",
            "howToRead": "x",
            "glossary": [{"id": "G.x", "term": "X", "definition": "X."}],
            "epics": [{
                "id": "E1", "title": "T", "framing": "F",
                "stories": [{
                    "id": "E1.S1", "role": "r", "want": "w", "soThat": "s",
                    "acceptanceCriteria": [
                        {"id": "E1.S1.A1", "text": "first"},
                        {"id": "E1.S1.A1", "text": "dup"},
                    ]
                }]
            }]
        }
        with open(os.path.join(bad_dir, "facit.json"), "w") as f:
            json.dump(bad_doc, f)
        rc, out = run(["--root", bad_dir, "validate"])
        assert rc != 0, f"Expected non-zero for invalid facit, got {rc}:\n{out}"
        assert "ERROR" in out, f"Expected 'ERROR' in validate output:\n{out}"
    print("PASS test_validate_invalid_prints_errors_nonzero")


# E1.S3.A1 (part 1) — compile flags duplicate AC ids
def test_compile_flags_duplicate_id():
    """E1.S3.A1 (dup-id): compile flags two ACs sharing the same id."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = os.path.join(tmpdir, "spec")
        os.makedirs(spec_dir)
        doc = {
            "schemaVersion": 1,
            "scope": {"level": "engine"},
            "extends": None,
            "preamble": "x",
            "howToRead": "x",
            "glossary": [{"id": "G.x", "term": "X", "definition": "X."}],
            "epics": [{
                "id": "E1", "title": "T", "framing": "F",
                "stories": [{
                    "id": "E1.S1", "role": "r", "want": "w", "soThat": "s",
                    "acceptanceCriteria": [
                        {"id": "E1.S1.A1", "text": "first"},
                        {"id": "E1.S1.A1", "text": "second — DUPLICATE ID"},
                    ]
                }]
            }]
        }
        with open(os.path.join(spec_dir, "facit.json"), "w") as f:
            json.dump(doc, f)
        rc, out = run(["--root", spec_dir, "compile"])
        assert rc != 0, f"Expected non-zero for duplicate id, got {rc}:\n{out}"
        assert "duplicate" in out.lower(), (
            f"Expected 'duplicate' in compile output:\n{out}")
    print("PASS test_compile_flags_duplicate_id")


# E1.S3.A1 (part 2) — compile flags an AC id not parent-bound to its story
def test_compile_flags_non_parent_bound_ac():
    """E1.S3.A1 (non-parent-bound): compile flags an AC id that is not parent-bound to its story."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = os.path.join(tmpdir, "spec")
        os.makedirs(spec_dir)
        doc = {
            "schemaVersion": 1,
            "scope": {"level": "engine"},
            "extends": None,
            "preamble": "x",
            "howToRead": "x",
            "glossary": [{"id": "G.x", "term": "X", "definition": "X."}],
            "epics": [{
                "id": "E1", "title": "T", "framing": "F",
                "stories": [{
                    "id": "E1.S1", "role": "r", "want": "w", "soThat": "s",
                    "acceptanceCriteria": [
                        # AC id starts with "E1.S2." but the story is "E1.S1" — not parent-bound
                        {"id": "E1.S2.A1", "text": "Not bound to E1.S1."},
                    ]
                }]
            }]
        }
        with open(os.path.join(spec_dir, "facit.json"), "w") as f:
            json.dump(doc, f)
        rc, out = run(["--root", spec_dir, "compile"])
        assert rc != 0, (
            f"Expected non-zero for non-parent-bound AC, got {rc}:\n{out}")
        assert "E1.S2.A1" in out or "not bound" in out.lower() or "bound" in out.lower(), (
            f"Expected non-parent-bound error in output:\n{out}")
    print("PASS test_compile_flags_non_parent_bound_ac")


# E1.S3.A2 — compile warns (not errors) when extends resolves to no known facit
def test_compile_warns_unknown_extends():
    """E1.S3.A2: compile warns (not fatal) when extends names an unknown facit."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        facit_path = os.path.join(spec_dir, "facit.json")
        with open(facit_path) as f:
            doc = json.load(f)
        doc["extends"] = "domain:nonexistent-scope-xyz"
        with open(facit_path, "w") as f:
            json.dump(doc, f)

        rc, out = run(["--root", spec_dir, "compile"])
        assert rc == 0, (
            f"Expected rc 0 (warning is not fatal), got {rc}:\n{out}")
        assert "warn" in out.lower(), (
            f"Expected a warning in compile output:\n{out}")
        assert "nonexistent-scope-xyz" in out or "extends" in out.lower(), (
            f"Expected extends warning text in output:\n{out}")
    print("PASS test_compile_warns_unknown_extends")


# E1.S3.A3 — compile warns when a target facit recommends a utility with no facit
def test_compile_warns_unknown_recommended_utility():
    """E1.S3.A3: compile warns when a target facit's recommendedUtility has no utility facit."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = os.path.join(tmpdir, "spec")
        os.makedirs(spec_dir)
        doc = {
            "schemaVersion": 1,
            "scope": {"level": "target", "domain": "testdomain", "target": "testtarget"},
            "extends": None,   # null avoids the extends-unknown warning
            "preamble": "x",
            "howToRead": "x",
            "glossary": [{"id": "G.x", "term": "X", "definition": "X."}],
            "uses": {
                "recommendedUtilities": ["nonexistent-util-xyz"]
            },
            "epics": [{
                "id": "E1", "title": "T", "framing": "F",
                "stories": [{
                    "id": "E1.S1", "role": "r", "want": "w", "soThat": "s",
                    "acceptanceCriteria": [
                        {"id": "E1.S1.A1", "text": "Given A, when B, then C."}
                    ]
                }]
            }]
        }
        with open(os.path.join(spec_dir, "facit.json"), "w") as f:
            json.dump(doc, f)

        rc, out = run(["--root", spec_dir, "compile"])
        assert rc == 0, (
            f"Expected rc 0 (utility warning is not fatal), got {rc}:\n{out}")
        assert "warn" in out.lower(), (
            f"Expected a warning in compile output:\n{out}")
        assert "nonexistent-util-xyz" in out or "utility" in out.lower(), (
            f"Expected utility warning text in output:\n{out}")
    print("PASS test_compile_warns_unknown_recommended_utility")


# E1.S5.A1 — lock writes per-node hash/status/tests and top-level lockedAt/facitHash
def test_lock_writes_per_node_hash_status_tests_and_metadata():
    """E1.S5.A1: lock writes lockedAt + facitHash at top level and hash/status/tests per node."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert "lockedAt" in lock, f"Expected 'lockedAt' in lock top level: {list(lock.keys())}"
        assert "facitHash" in lock, f"Expected 'facitHash' in lock top level: {list(lock.keys())}"

        nodes = lock.get("nodes", {})
        assert len(nodes) > 0, "Expected at least one node in lock"
        for qid, node in nodes.items():
            assert "hash" in node, f"Node {qid} missing 'hash': {node}"
            assert "status" in node, f"Node {qid} missing 'status': {node}"
            assert "tests" in node, f"Node {qid} missing 'tests': {node}"
    print("PASS test_lock_writes_per_node_hash_status_tests_and_metadata")


# E1.S6.A1 — diff lists added, changed, and removed nodes
def test_diff_lists_added_changed_and_removed():
    """E1.S6.A1: diff reports added, changed, and removed nodes."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_two_acs(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        facit_path = os.path.join(spec_dir, "facit.json")
        with open(facit_path) as f:
            doc = json.load(f)

        acs = doc["epics"][0]["stories"][0]["acceptanceCriteria"]
        # CHANGE: modify A1 text
        acs[0]["text"] = acs[0]["text"] + " [CHANGED]"
        # REMOVE: remove A2 (index 1)
        acs.pop(1)
        # ADD: add a brand-new A3
        acs.append({"id": "E1.S1.A3", "text": "Given G, when H, then I."})

        with open(facit_path, "w") as f:
            json.dump(doc, f, indent=2)

        rc, out = run(["--root", spec_dir, "diff"])
        assert rc == 0, f"diff failed: {out}"
        assert "+ added" in out, f"Expected '+ added' in diff output:\n{out}"
        assert "~ changed" in out, f"Expected '~ changed' in diff output:\n{out}"
        assert "- removed" in out, f"Expected '- removed' in diff output:\n{out}"
    print("PASS test_diff_lists_added_changed_and_removed")


# E1.S7.A1 — status reports total/locked/proven/changed counts
def test_status_reports_counts():
    """E1.S7.A1: status reports total node count, locked, proven-current, and changed-since-lock."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        rc, out = run(["--root", spec_dir, "status"])
        assert rc == 0, f"status failed: {out}"
        assert "nodes:" in out, f"Expected 'nodes:' in status output:\n{out}"
        assert "locked:" in out, f"Expected 'locked:' in status output:\n{out}"
        assert "proven(current):" in out, f"Expected 'proven(current):' in status output:\n{out}"
        assert "changed-since-lock:" in out, f"Expected 'changed-since-lock:' in status output:\n{out}"

        # Minimal facit: 1 story + 1 AC = 2 nodes; freshly locked → 0 proven, 0 changed
        assert "nodes: 2" in out, f"Expected 'nodes: 2' in status output:\n{out}"
        assert "locked: 2" in out, f"Expected 'locked: 2' in status output:\n{out}"
        assert "proven(current): 0" in out, f"Expected 'proven(current): 0' in status output:\n{out}"
        assert "changed-since-lock: 0" in out, (
            f"Expected 'changed-since-lock: 0' in status output:\n{out}")
    print("PASS test_status_reports_counts")


# E1.S7.A2 — gap with no file lists every AC id
def test_gap_no_file_lists_every_ac():
    """E1.S7.A2: gap with no file arg lists every AC id (globally-qualified) in the compiled facit."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_two_acs(tmpdir)
        rc, out = run(["--root", spec_dir, "gap"])
        assert rc == 0, f"gap (no file) failed: {out}"
        # Both ACs must appear as globally-qualified ids in the output
        assert "engine::E1.S1.A1" in out, (
            f"Expected 'engine::E1.S1.A1' in gap output:\n{out}")
        assert "engine::E1.S1.A2" in out, (
            f"Expected 'engine::E1.S1.A2' in gap output:\n{out}")
        # The count goes to stderr (combined via run()); 2 ACs in the facit
        assert "2 acceptance criteria" in out, (
            f"Expected '2 acceptance criteria' count in gap output:\n{out}")
    print("PASS test_gap_no_file_lists_every_ac")


# E1.S7.A3 — gap with a file reports covered/uncovered/unknown
def test_gap_file_reports_coverage_and_flags_unknown():
    """E1.S7.A3: gap with a file reports counts and flags unknown acIds."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)

        # Gap file: one valid local acId, one unknown local acId
        gap_file = os.path.join(tmpdir, "gap.json")
        gap_doc = {
            "items": [
                {"acId": "E1.S1.A1"},   # valid — exists in the minimal facit
                {"acId": "E9.S9.A9"},   # unknown — no such node
            ]
        }
        with open(gap_file, "w") as f:
            json.dump(gap_doc, f)

        rc, out = run(["--root", spec_dir, "gap", gap_file])
        # Returns non-zero when unknown acIds are present
        assert rc != 0, f"Expected non-zero when unknown acId present, got {rc}:\n{out}"

        # Must report summary counts
        assert "gap entries:" in out, f"Expected 'gap entries:' in gap output:\n{out}"
        assert "unknown-acId:" in out, f"Expected 'unknown-acId:' in gap output:\n{out}"

        # Must explicitly flag the unknown acId
        assert "E9.S9.A9" in out, (
            f"Expected unknown acId 'E9.S9.A9' listed in gap output:\n{out}")
        # cmd_gap prints: "  unknown acId: <id>" — check the literal label
        assert "unknown acId" in out, (
            f"Expected 'unknown acId' label in gap output:\n{out}")
    print("PASS test_gap_file_reports_coverage_and_flags_unknown")


# E1.S9.A4 — conform is incremental: only ACs whose covered files drifted are re-opened
def test_conform_incremental_only_drifted():
    """E1.S9.A4: conform re-opens only ACs whose covered files changed; others stay conformant."""
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_conform_incremental_only_drifted (coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_two_acs(tmpdir)

        # Two separate impl files — one per AC
        impl_a = os.path.join(tmpdir, "impl_a.py")
        impl_b = os.path.join(tmpdir, "impl_b.py")
        with open(impl_a, "w") as f:
            f.write("x = 1\n")
        with open(impl_b, "w") as f:
            f.write("y = 2\n")

        test_a = "test_for_a"
        test_b = "test_for_b"

        # Lock
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        # Implementation map: A1 bound to test_a / impl_a; A2 bound to test_b / impl_b
        impl = {
            "schemaVersion": 1, "facitVersion": "test",
            "entries": [
                {
                    "acId": "E1.S1.A1", "status": "implemented",
                    "evidence": [{"kind": "code", "ref": impl_a}],
                    "proof": {"tests": [test_a], "coveredFiles": []},
                },
                {
                    "acId": "E1.S1.A2", "status": "implemented",
                    "evidence": [{"kind": "code", "ref": impl_b}],
                    "proof": {"tests": [test_b], "coveredFiles": []},
                },
            ]
        }
        impl_path = os.path.join(tmpdir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # Coverage DB: test_a covers impl_a; test_b covers impl_b
        cov_path = os.path.join(tmpdir, ".coverage")
        cd = coverage_mod.CoverageData(basename=cov_path)
        cd.set_context(test_a)
        cd.add_lines({impl_a: {1}})
        cd.set_context(test_b)
        cd.add_lines({impl_b: {1}})
        cd.write()

        # TRX: both tests pass
        trx_path = os.path.join(tmpdir, "results.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_a: "Passed", test_b: "Passed"}))

        # Prove with per-test coverage; src_root = tmpdir so both impl files are in scope
        rc, out = run(["--root", spec_dir, "prove",
                       "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path, "--src-root", tmpdir])
        assert rc == 0, f"prove failed: {out}"

        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert lock["nodes"]["engine::E1.S1.A1"]["status"] == "proven", "A1 must be proven"
        assert lock["nodes"]["engine::E1.S1.A2"]["status"] == "proven", "A2 must be proven"
        assert lock["nodes"]["engine::E1.S1.A1"].get("coveredFiles"), "A1 must have coveredFiles"
        assert lock["nodes"]["engine::E1.S1.A2"].get("coveredFiles"), "A2 must have coveredFiles"

        # conform: clean initially
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc == 0, f"conform should be clean after fresh prove: {out}"
        assert "0 drifted" in out, f"Expected '0 drifted': {out}"

        # Modify ONLY impl_a.py → only A1 should drift
        with open(impl_a, "w") as f:
            f.write("x = 99  # changed\n")

        rc, out = run(["--root", spec_dir, "conform"])
        assert rc != 0, f"conform should exit non-zero when A1 is drifted: {out}"

        # Exactly one DRIFTED node (A1); A2 must still be CONFORMANT
        drifted_lines = [ln for ln in out.splitlines() if "DRIFTED:" in ln]
        assert len(drifted_lines) == 1, (
            f"Expected exactly 1 DRIFTED node, got {len(drifted_lines)}:\n{out}")
        assert "engine::E1.S1.A1" in drifted_lines[0], (
            f"Expected DRIFTED to be A1: {drifted_lines[0]}")

        assert "1 conformant" in out, (
            f"Expected '1 conformant' (A2 unchanged) in conform output:\n{out}")
        assert "1 drifted" in out, (
            f"Expected '1 drifted' (A1 changed) in conform output:\n{out}")
    print("PASS test_conform_incremental_only_drifted")


# E1.S10 regression — coverage gate normalises absolute vs repo-relative paths
def test_coverage_gate_normalizes_absolute_vs_relative_paths():
    """E1.S10 regression: prove accepts when coverage records abs path and impl declares rel path.

    coverage.py stores absolute paths; implementation.json declares repo-relative paths.
    Without the _rel() normalisation in cmd_prove, the intersection is empty → COVERAGE-MISS.
    This test pins that the normalisation runs on both sides before the intersection check.
    """
    try:
        import coverage as coverage_mod
    except ImportError:
        print("SKIP test_coverage_gate_normalizes_absolute_vs_relative_paths (coverage not installed)")
        return

    # Create the impl file INSIDE the repo root so _rel() produces a repo-relative path
    test_impl_dir = os.path.join(REPO_ROOT, "tools", "facit", "tests", "_tmp_cov_norm_test")
    try:
        os.makedirs(test_impl_dir, exist_ok=True)
        impl_file_abs = os.path.join(test_impl_dir, "impl_norm_x.py")
        with open(impl_file_abs, "w") as f:
            f.write("z = 1\n")

        # Repo-relative path (as implementation.json would declare it)
        impl_file_rel = os.path.relpath(impl_file_abs, REPO_ROOT)

        with tempfile.TemporaryDirectory() as tmpdir:
            spec_dir = _make_minimal_facit(tmpdir)
            test_id = "test_norm_x"

            # Lock
            rc, out = run(["--root", spec_dir, "lock"])
            assert rc == 0, f"lock failed: {out}"

            # Impl entry: code evidence uses the REPO-RELATIVE path
            impl = {
                "schemaVersion": 1, "facitVersion": "test",
                "entries": [{
                    "acId": "E1.S1.A1", "status": "implemented",
                    "evidence": [{"kind": "code", "ref": impl_file_rel}],
                    "proof": {"tests": [test_id], "coveredFiles": []},
                }]
            }
            impl_path = os.path.join(tmpdir, "implementation.json")
            with open(impl_path, "w") as f:
                json.dump(impl, f, indent=2)

            # Coverage DB: records ABSOLUTE path (coverage.py always uses abs)
            cov_path = os.path.join(tmpdir, ".coverage")
            cd = coverage_mod.CoverageData(basename=cov_path)
            cd.set_context(test_id)
            cd.add_lines({impl_file_abs: {1}})   # ← absolute path
            cd.write()

            # TRX: test passes
            trx_path = os.path.join(tmpdir, "results.trx")
            with open(trx_path, "w") as f:
                f.write(_make_trx({test_id: "Passed"}))

            # prove: should PASS — normalisation must make abs ≡ rel before intersecting.
            # --src-root must include test_impl_dir so _compute_covered_files doesn't filter
            # out the covered file (which lives inside the repo, not in tmpdir).
            rc, out = run(["--root", spec_dir, "prove",
                           "--impl", impl_path, "--results", trx_path,
                           "--coverage", cov_path, "--src-root", test_impl_dir])
            assert rc == 0, (
                f"Expected rc 0 (normalisation makes abs ≡ rel), got {rc}:\n{out}")
            assert "COVERAGE-MISS" not in out, (
                f"COVERAGE-MISS must not fire when paths normalise to the same file:\n{out}")

            lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
            node = lock["nodes"].get("engine::E1.S1.A1")
            assert node is not None and node["status"] == "proven", (
                f"Expected AC proven after normalised coverage intersection, got: {node}")
    finally:
        shutil.rmtree(test_impl_dir, ignore_errors=True)

    print("PASS test_coverage_gate_normalizes_absolute_vs_relative_paths")


def test_selfverify_proven_cli_acs_are_coverage_backed_and_conform_clean():
    """E1.S11.A3 — invariant on the CLI's OWN lock: every proven CLI AC carries a non-empty
    coverage-backed binding (coveredFiles) and conform reports the facit clean. Runs `conform`
    in-process (under selfproof) so this test itself executes facit.py's conform path under
    coverage — making this proof coverage-backed by the same standard it asserts."""
    lock = read_lock(CLI_LOCK)
    proven = [(q, n) for q, n in lock["nodes"].items()
              if n.get("status") == "proven" and q.count(".") == 2]
    assert proven, "expected the CLI's own facit to have proven ACs"
    uncovered = [q for q, n in proven if not n.get("coveredFiles")]
    assert not uncovered, (
        f"every proven CLI AC must carry a coverage-backed binding; "
        f"these have empty coveredFiles: {uncovered}")
    rc, out = run(["--root", "tools/facit/spec", "conform"])
    assert rc == 0, f"conform must be clean on the CLI's own facit (exit 0):\n{out}"
    assert ", 0 drifted," in out, f"expected 0 drifted in conform summary:\n{out}"
    print("PASS test_selfverify_proven_cli_acs_are_coverage_backed_and_conform_clean")


# ---------------------------------------------------------------------------
# Hardened-gate behavior pins (2026-07-01 audit fixes)
# ---------------------------------------------------------------------------

def test_prove_refuses_without_coverage():
    """prove refuses to mark an AC proven without a populated coveredFiles trigger (E1.S9.A5)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "NS.C.t_pass"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])
        trx_path = os.path.join(tmpdir, "r.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx_path])
        assert rc != 0, f"prove without --coverage must refuse (strict), got rc={rc}:\n{out}"
        assert "NO-COVERAGE" in out, f"expected NO-COVERAGE in output:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", f"must not be proven:\n{node}"
    print("PASS test_prove_refuses_without_coverage")


def test_prove_demotes_proven_on_red():
    """A previously-proven AC whose bound test is now Failed is DEMOTED to unproven (E1.S8.A8)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "NS.C.t_x"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])
        cov_path = _make_covdb(tmpdir, [test_id])
        if cov_path is None:
            print("SKIP test_prove_demotes_proven_on_red (coverage not installed)")
            return
        green = os.path.join(tmpdir, "green.trx")
        with open(green, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path,
                       "--results", green, "--coverage", cov_path])
        assert rc == 0, f"initial prove must pass:\n{out}"
        assert read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]["status"] == "proven"
        # The bound test now goes red → a red test un-proves its AC.
        red = os.path.join(tmpdir, "red.trx")
        with open(red, "w") as f:
            f.write(_make_trx({test_id: "Failed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path,
                       "--results", red, "--coverage", cov_path])
        assert rc != 0, f"prove with a red bound test must exit non-zero:\n{out}"
        assert "DEMOTED" in out, f"expected DEMOTED in output:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert node["status"] == "unproven", f"a red test must un-prove: {node}"
        assert node["tests"] == [], f"tests must be cleared on demotion: {node}"
        assert not node.get("coveredFiles"), f"coveredFiles must be cleared on demotion: {node}"
    print("PASS test_prove_demotes_proven_on_red")


def test_verify_rechecks_greenness_with_results():
    """prove --verify --results re-checks the bound test is Passed NOW; without --results it does not (E1.S8.A9)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "NS.C.t_g"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])
        cov_path = _make_covdb(tmpdir, [test_id])
        if cov_path is None:
            print("SKIP test_verify_rechecks_greenness_with_results (coverage not installed)")
            return
        green = os.path.join(tmpdir, "green.trx")
        with open(green, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path,
                       "--results", green, "--coverage", cov_path])
        assert rc == 0, out
        rc, out = run(["--root", spec_dir, "prove", "--verify", "--results", green])
        assert rc == 0, f"verify with green results must pass:\n{out}"
        red = os.path.join(tmpdir, "red.trx")
        with open(red, "w") as f:
            f.write(_make_trx({test_id: "Failed"}))
        rc, out = run(["--root", spec_dir, "prove", "--verify", "--results", red])
        assert rc != 0, f"verify must fail when the bound test is now red:\n{out}"
        assert "GREEN-NOW" in out, f"expected TEST-NOT-GREEN-NOW:\n{out}"
        rc, out = run(["--root", spec_dir, "prove", "--verify"])
        assert rc == 0, f"verify without results must pass on a coverage-backed proven node:\n{out}"
    print("PASS test_verify_rechecks_greenness_with_results")


def test_verify_fails_on_proven_without_coverage():
    """prove --verify fails on a proven lock node lacking a coveredFiles trigger (strict, E1.S9.A3)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        run(["--root", spec_dir, "lock"])
        lock_path = os.path.join(spec_dir, "facit.lock.json")
        lock = read_lock(lock_path)
        for q, n in lock["nodes"].items():
            if q.endswith("E1.S1.A1"):
                n["status"] = "proven"
                n["tests"] = ["x"]  # NO coveredFiles
        with open(lock_path, "w") as f:
            json.dump(lock, f, indent=2)
        rc, out = run(["--root", spec_dir, "prove", "--verify"])
        assert rc != 0, f"verify must fail on proven-without-coverage:\n{out}"
        assert "WITHOUT-COVERAGE" in out, f"expected PROVEN-WITHOUT-COVERAGE:\n{out}"
    print("PASS test_verify_fails_on_proven_without_coverage")


def test_dupname_bare_collision_refused_fqn_still_works():
    """A bare display-name shared by two different tests with differing outcomes fails closed;
    binding by fully-qualified name still resolves (E1.S8.A7)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        trx = _make_trx_with_defs([
            {"id": "1", "displayName": "Roundtrips", "className": "NS.ClassA",
             "methodName": "Roundtrips", "outcome": "Failed"},
            {"id": "2", "displayName": "Roundtrips", "className": "NS.ClassB",
             "methodName": "Roundtrips", "outcome": "Passed"},
        ])
        trx_path = os.path.join(tmpdir, "r.trx")
        with open(trx_path, "w") as f:
            f.write(trx)
        # Bind by BARE name → must refuse as ambiguous (fail closed, not last-writer-wins).
        impl_bare = _make_impl(tmpdir, spec_dir, ["Roundtrips"])
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_bare, "--results", trx_path])
        assert rc != 0, f"bare-name collision must refuse:\n{out}"
        assert "mbiguous" in out.lower(), f"expected an ambiguous refusal:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", f"must not prove on ambiguity:\n{node}"
        # Bind by FQN → must NOT be ambiguous (resolves to the specific test).
        impl_fqn = _make_impl(tmpdir, spec_dir, ["NS.ClassB.Roundtrips"])
        rc2, out2 = run(["--root", spec_dir, "prove", "--impl", impl_fqn, "--results", trx_path])
        assert "mbiguous" not in out2.lower(), f"FQN binding must resolve, not be ambiguous:\n{out2}"
    print("PASS test_dupname_bare_collision_refused_fqn_still_works")


def test_atomic_write_no_tmp_leftover():
    """_write is atomic: the lock parses as JSON and no .facit-tmp-* file is left behind (E1.S5.A4)."""
    import glob as _glob
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, out
        lock_path = os.path.join(spec_dir, "facit.lock.json")
        with open(lock_path) as f:
            json.load(f)  # must parse — never truncated/partial
        leftovers = _glob.glob(os.path.join(spec_dir, ".facit-tmp-*"))
        assert not leftovers, f"atomic write must leave no temp file behind: {leftovers}"
    print("PASS test_atomic_write_no_tmp_leftover")


def test_selfproof_verdict_does_not_discard_prove_rc():
    """selfproof._verdict fails if ANY stage failed — prove_rc is NOT discarded (E1.S11.A4)."""
    sp = _import_selfproof_module()
    assert sp._verdict(0, 0, 0)[0] == 0, "all-green must pass"
    assert sp._verdict(1, 0, 0)[0] == 1, "a red test suite must fail"
    assert sp._verdict(0, 1, 0)[0] == 1, "a prove refusal must fail (SP-1: rc not discarded)"
    assert sp._verdict(0, 0, 1)[0] == 1, "a conform drift must fail"
    print("PASS test_selfproof_verdict_does_not_discard_prove_rc")


def test_prove_populates_coveredfiles_from_aggregate_cobertura_without_evidence():
    """E1.S9.A6: aggregate cobertura + no declared code evidence → coveredFiles populated
    from every covered source file under src-root (supports .NET cobertura)."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "NS.C.t_cob"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])  # declares a test evidence only, no code
        srcroot = os.path.join(tmpdir, "src")
        os.makedirs(srcroot)
        with open(os.path.join(srcroot, "Widget.cs"), "w") as f:
            f.write("class Widget {}\n")
        cob = os.path.join(tmpdir, "coverage.cobertura.xml")
        with open(cob, "w") as f:
            f.write(
                '<?xml version="1.0"?>\n'
                f'<coverage><sources><source>{srcroot}</source></sources>\n'
                '<packages><package><classes>\n'
                '<class filename="Widget.cs"><lines><line number="1" hits="1"/></lines></class>\n'
                '</classes></package></packages></coverage>\n')
        trx_path = os.path.join(tmpdir, "r.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx_path,
                       "--coverage", cob, "--src-root", srcroot])
        assert rc == 0, f"prove with aggregate cobertura + no evidence must pass:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert node["status"] == "proven", node
        paths = [c["path"] for c in node.get("coveredFiles", [])]
        assert any("Widget.cs" in p for p in paths), f"coveredFiles must include the covered source: {paths}"
    print("PASS test_prove_populates_coveredfiles_from_aggregate_cobertura_without_evidence")


# ---------------------------------------------------------------------------
# E1.S12 — self-hashing: the proving test's own source is hash-tracked
# ---------------------------------------------------------------------------

def test_find_method_span_python_and_csharp():
    """_find_method_span locates a method's exact line span in Python and C# (E1.S12.A4)."""
    m = _import_facit_module()
    py = ["import x", "", "def alpha():", "    return 1", "", "def target(a):",
          "    y = a + 1", "    return y", "", "def omega():", "    pass"]
    assert m._find_method_span(py, "target", True) == (6, 8), m._find_method_span(py, "target", True)
    cs = ["class C {", "    public void Alpha() { }", "    public void Target()", "    {",
          "        DoThing();", "    }", "    public void Omega() { }", "}"]
    assert m._find_method_span(cs, "Target", False) == (3, 6), m._find_method_span(cs, "Target", False)
    print("PASS test_find_method_span_python_and_csharp")


def _write_proving_test_file(tmpdir, method, body_line):
    """Create a fixture test source file defining `method`; returns (dir, path)."""
    tdir = os.path.join(tmpdir, "ftests")
    os.makedirs(tdir, exist_ok=True)
    path = os.path.join(tdir, "fixture_tests.py")
    with open(path, "w") as f:
        f.write(f"def {method}():\n    x = 1  # {body_line}\n    assert x == 1\n")
    return tdir, path


def test_prove_records_test_source_hash(tmpdir=None):
    """prove records each proving test's source location + line-range hash (E1.S12.A4)."""
    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)
        method = "my_proving_test"
        tdir, tpath = _write_proving_test_file(tmp, method, "original")
        impl_path = _make_impl(tmp, spec_dir, [method])
        cov_path = _make_covdb(tmp, [method])
        if cov_path is None:
            print("SKIP test_prove_records_test_source_hash (coverage not installed)")
            return
        trx = os.path.join(tmp, "r.trx")
        with open(trx, "w") as f:
            f.write(_make_trx({method: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                       "--coverage", cov_path, "--test-root", tdir])
        assert rc == 0, f"prove must pass:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        ts = node.get("testSources", [])
        assert ts and ts[0]["path"] == os.path.realpath(os.path.abspath(tpath)), f"expected testSources for the proving test: {node}"
        assert ts[0]["startLine"] == 1 and ts[0]["endLine"] == 3, ts[0]
        assert len(ts[0]["hash"]) == 16, ts[0]
    print("PASS test_prove_records_test_source_hash")


def test_conform_detects_test_source_drift():
    """conform flags an AC whose proving-test source changed — re-run + re-review (E1.S12.A5/A6)."""
    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)
        method = "my_proving_test"
        tdir, tpath = _write_proving_test_file(tmp, method, "original")
        impl_path = _make_impl(tmp, spec_dir, [method])
        cov_path = _make_covdb(tmp, [method])
        if cov_path is None:
            print("SKIP test_conform_detects_test_source_drift (coverage not installed)")
            return
        trx = os.path.join(tmp, "r.trx")
        with open(trx, "w") as f:
            f.write(_make_trx({method: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                       "--coverage", cov_path, "--test-root", tdir])
        assert rc == 0, out
        # conform is clean before the test changes
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc == 0, f"conform should be clean initially:\n{out}"
        # Now weaken the proving test's body (same line count) → hash mismatch → re-review
        with open(tpath, "w") as f:
            f.write(f"def {method}():\n    x = 999  # gutted\n    assert True\n")
        rc, out = run(["--root", spec_dir, "conform"])
        assert rc != 0, f"conform must fail when the proving test's source changed:\n{out}"
        assert "re-review" in out.lower(), f"expected the test-drift 're-review' message:\n{out}"
    print("PASS test_conform_detects_test_source_drift")


# ---------------------------------------------------------------------------
# E1.S12.A1/A2 — reverify runs tests itself and proves
# E1.S12.A3    — incremental scope selects only affected ACs
# ---------------------------------------------------------------------------

def test_reverify_runs_tests_and_proves():
    """reverify uses the configured testCommand to run tests, then proves (E1.S12.A1/A2)."""
    try:
        import pytest  # noqa: F401
        import coverage as _cov  # noqa: F401
    except ImportError:
        print("SKIP test_reverify_runs_tests_and_proves (pytest/coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)

        # Source file that the fixture test will import (so coverage sees it)
        src_dir = os.path.join(tmp, "src")
        os.makedirs(src_dir)
        src_file = os.path.join(src_dir, "impl_rv.py")
        with open(src_file, "w") as f:
            f.write("def do_it():\n    return 42\n")

        # Fixture test file
        test_dir = os.path.join(tmp, "ftests")
        os.makedirs(test_dir)
        test_file = os.path.join(test_dir, "test_fixture_rv.py")
        test_name = "test_proving_rv_ac1"
        with open(test_file, "w") as f:
            f.write(
                f"import sys, os\n"
                f"sys.path.insert(0, {repr(src_dir)})\n"
                f"from impl_rv import do_it\n"
                f"def {test_name}():\n"
                f"    assert do_it() == 42\n"
            )

        # .coveragerc for the fixture
        crc = os.path.join(tmp, ".coveragerc_rv")
        with open(crc, "w") as f:
            f.write(f"[run]\nsource = {src_dir}\nomit = */tests/*\n")

        # facit.config.json inside spec_dir
        test_cmd = (
            f"COVERAGE_FILE={{outdir}}/.coverage "
            f"{sys.executable} -m pytest {test_file} "
            f"--cov={src_dir} --cov-config={crc} "
            f"--cov-context=test --cov-report= "
            f"--junitxml={{outdir}}/junit.xml -p no:cacheprovider -q "
            f"-k \"{{filter}}\""
        )
        cfg = {
            "testRoots": [test_dir],
            "testCommand": test_cmd,
            "filterItem": "{test}",
            "filterJoin": " or ",
        }
        with open(os.path.join(spec_dir, "facit.config.json"), "w") as f:
            json.dump(cfg, f, indent=2)

        # Lock, then write implementation.json
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        impl = {
            "schemaVersion": 1,
            "facitVersion": "test",
            "entries": [{
                "acId": "E1.S1.A1",
                "status": "implemented",
                "evidence": [
                    {"kind": "code", "ref": src_file, "note": "impl_rv.py"},
                    {"kind": "test", "ref": test_name, "note": "proving test"},
                ],
                "proof": {"tests": [test_name], "coveredFiles": []},
            }],
        }
        impl_path = os.path.join(spec_dir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # reverify --ac E1.S1.A1  →  must prove
        rc, out = run(["--root", spec_dir, "reverify",
                       "--impl", impl_path,
                       "--ac", "E1.S1.A1",
                       "--src-root", src_dir])
        assert rc == 0, f"reverify must prove the AC:\n{out}"

        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"].get("engine::E1.S1.A1")
        assert node is not None and node["status"] == "proven", f"expected proven: {node}"
        assert node.get("coveredFiles"), f"expected non-empty coveredFiles: {node}"
        assert node.get("testSources"), f"expected testSources recorded (hash-tracked): {node}"

    print("PASS test_reverify_runs_tests_and_proves")


def test_reverify_incremental_scope_selects_only_affected():
    """reverify default (incremental) re-runs only ACs with drift; unaffected ACs are skipped (E1.S12.A3)."""
    try:
        import pytest  # noqa: F401
        import coverage as _cov  # noqa: F401
    except ImportError:
        print("SKIP test_reverify_incremental_scope_selects_only_affected (pytest/coverage not installed)")
        return

    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_facit_two_acs(tmp)

        test_a = "test_rv_ac1"
        test_b = "test_rv_ac2"

        src_dir = os.path.join(tmp, "src")
        os.makedirs(src_dir)
        src_a = os.path.join(src_dir, "impl_rv_a.py")
        src_b = os.path.join(src_dir, "impl_rv_b.py")
        with open(src_a, "w") as f:
            f.write("def do_a(): return 1\n")
        with open(src_b, "w") as f:
            f.write("def do_b(): return 2\n")

        test_dir = os.path.join(tmp, "ftests")
        os.makedirs(test_dir)
        test_file = os.path.join(test_dir, "test_fixture_inc.py")

        def _write_test_file(body_a="assert do_a() == 1"):
            with open(test_file, "w") as f:
                f.write(
                    f"import sys\nsys.path.insert(0, {repr(src_dir)})\n"
                    f"from impl_rv_a import do_a\nfrom impl_rv_b import do_b\n"
                    f"def {test_a}():\n    {body_a}\n"
                    f"def {test_b}():\n    assert do_b() == 2\n"
                )

        _write_test_file()

        crc = os.path.join(tmp, ".coveragerc_inc")
        with open(crc, "w") as f:
            f.write(f"[run]\nsource = {src_dir}\nomit = */tests/*\n")

        log_file = os.path.join(tmp, "filter_log.txt")

        test_cmd = (
            f"printf '%s\\n' \"{{filter}}\" >> {log_file} && "
            f"COVERAGE_FILE={{outdir}}/.coverage "
            f"{sys.executable} -m pytest {test_file} "
            f"--cov={src_dir} --cov-config={crc} "
            f"--cov-context=test --cov-report= "
            f"--junitxml={{outdir}}/junit.xml -p no:cacheprovider -q "
            f"-k \"{{filter}}\""
        )
        cfg = {
            "testRoots": [test_dir],
            "testCommand": test_cmd,
            "filterItem": "{test}",
            "filterJoin": " or ",
        }
        with open(os.path.join(spec_dir, "facit.config.json"), "w") as f:
            json.dump(cfg, f, indent=2)

        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, f"lock failed: {out}"

        impl = {
            "schemaVersion": 1, "facitVersion": "test",
            "entries": [
                {
                    "acId": "E1.S1.A1", "status": "implemented",
                    "evidence": [{"kind": "code", "ref": src_a}],
                    "proof": {"tests": [test_a], "coveredFiles": []},
                },
                {
                    "acId": "E1.S1.A2", "status": "implemented",
                    "evidence": [{"kind": "code", "ref": src_b}],
                    "proof": {"tests": [test_b], "coveredFiles": []},
                },
            ],
        }
        impl_path = os.path.join(spec_dir, "implementation.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f, indent=2)

        # Initial --all to prove both ACs and record testSources
        rc, out = run(["--root", spec_dir, "reverify",
                       "--impl", impl_path, "--all", "--src-root", src_dir])
        assert rc == 0, f"initial reverify --all must pass:\n{out}"
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert lock["nodes"]["engine::E1.S1.A1"]["status"] == "proven", "A1 must be proven"
        assert lock["nodes"]["engine::E1.S1.A2"]["status"] == "proven", "A2 must be proven"
        ts1 = lock["nodes"]["engine::E1.S1.A1"].get("testSources", [])
        assert ts1, "A1 must have testSources for drift detection to work"

        # Reset the log, then dirty only A1's proving-test source body
        with open(log_file, "w") as f:
            pass
        _write_test_file(body_a="assert do_a() == 1  # slightly changed — triggers test drift")

        # Incremental reverify (no --all, no --ac) — only A1 is affected
        rc, out = run(["--root", spec_dir, "reverify",
                       "--impl", impl_path, "--src-root", src_dir])
        assert rc == 0, f"incremental reverify must succeed (test still passes):\n{out}"

        with open(log_file) as f:
            log_content = f.read()

        assert test_a in log_content, (
            f"expected {test_a!r} in filter log (A1 was drifted):\n{log_content!r}")
        assert test_b not in log_content, (
            f"expected {test_b!r} NOT in filter log (A2 was not drifted):\n{log_content!r}")

    print("PASS test_reverify_incremental_scope_selects_only_affected")


def test_relock_preserves_testsources():
    """Re-lock carries forward BOTH coveredFiles and testSources for an unchanged node (E1.S5.A3)
    — dropping the test-drift trigger on re-lock would silently blind conform."""
    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)
        method = "my_proving_test"
        tdir, _tpath = _write_proving_test_file(tmp, method, "original")
        impl_path = _make_impl(tmp, spec_dir, [method])
        cov_path = _make_covdb(tmp, [method])
        if cov_path is None:
            print("SKIP test_relock_preserves_testsources (coverage not installed)")
            return
        trx = os.path.join(tmp, "r.trx")
        with open(trx, "w") as f:
            f.write(_make_trx({method: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                       "--coverage", cov_path, "--test-root", tdir])
        assert rc == 0, out
        before = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert before.get("testSources") and before.get("coveredFiles"), before
        # Re-lock (facit unchanged) → proof + both triggers must carry forward.
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, out
        after = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert after["status"] == "proven", after
        assert after.get("testSources") == before["testSources"], f"testSources dropped on re-lock: {after}"
        assert after.get("coveredFiles") == before["coveredFiles"], f"coveredFiles dropped on re-lock: {after}"
    print("PASS test_relock_preserves_testsources")


def test_compute_covered_files_matches_relative_declared_to_absolute_coverage():
    """_compute_covered_files matches repo-relative declared code evidence against absolute
    coverage paths (E1.S10.A1) — the abs/rel mismatch that made .NET reverify report NO-COVERAGE."""
    m = _import_facit_module()
    real_rel = "tools/facit/facit.py"            # repo-relative, as impl evidence is stored
    real_abs = os.path.join(m.REPO_ROOT, real_rel)  # absolute, as cobertura reports it
    entry = {"evidence": [{"kind": "code", "ref": real_rel + ":1-5"}]}
    res = m._compute_covered_files(["t"], entry, None, {real_abs}, m.REPO_ROOT)
    assert len(res) == 1 and "facit.py" in res[0]["path"], f"declared(rel) must match covered(abs): {res}"
    print("PASS test_compute_covered_files_matches_relative_declared_to_absolute_coverage")


def test_structural_proof_hashes_declared_code_without_coverage():
    """E1.S9.A7: a structural proof records the declared governed code file's hash as its
    code-drift trigger WITHOUT runtime coverage; a structural proof with no governed file refuses."""
    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, out
        method = "structural_reflection_check"
        # structural entry governs a real repo source file (facit.py), no --coverage supplied
        impl = {"schemaVersion": 1, "facitVersion": "t", "entries": [{
            "acId": "E1.S1.A1", "status": "implemented",
            "evidence": [{"kind": "code", "ref": "tools/facit/facit.py"}],
            "proof": {"structural": True, "tests": [method], "coveredFiles": []}}]}
        impl_path = os.path.join(tmp, "impl.json")
        with open(impl_path, "w") as f:
            json.dump(impl, f)
        trx = os.path.join(tmp, "r.trx")
        with open(trx, "w") as f:
            f.write(_make_trx({method: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                       "--src-root", "tools/facit"])
        assert rc == 0, f"structural proof must succeed without coverage:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert node["status"] == "proven", node
        paths = [c["path"] for c in node.get("coveredFiles", [])]
        assert any("facit.py" in p for p in paths), f"governed file must be the trigger: {paths}"
        # negative: structural proof declaring NO governed code file is refused
        impl["entries"][0]["evidence"] = []
        with open(impl_path, "w") as f:
            json.dump(impl, f)
        run(["--root", spec_dir, "lock"])  # reset the lock node
        rc2, out2 = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                         "--src-root", "tools/facit"])
        assert rc2 != 0, f"structural proof without a governed file must refuse:\n{out2}"
        assert "STRUCTURAL-NO-GOVERNED-FILE" in out2, out2
    print("PASS test_structural_proof_hashes_declared_code_without_coverage")


# ---------------------------------------------------------------------------
# Defect fixes: destructive prove with default src_root (mass-demote guard) +
# conform's inert proving-test-drift check + TRX Theory fail-open
# ---------------------------------------------------------------------------

def test_prove_refuses_mass_demote_of_all_proven_nodes():
    """DEFECT 1(b): prove refuses (loud, non-zero exit, lock untouched) instead of silently
    demoting EVERY currently-proven node in one pass -- almost always a configuration error
    (e.g. a wrong --src-root filtering all coverage out), not a genuine regression across the
    whole proven set. --allow-mass-demote overrides the refusal."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_facit_two_acs(tmpdir)
        rc, out = run(["--root", spec_dir, "lock"])
        assert rc == 0, out

        t1, t2 = "ProjA.ClsA.Test_one", "ProjB.ClsB.Test_two"
        impl1 = _write_impl(os.path.join(tmpdir, "impl1.json"), "E1.S1.A1", [t1])
        impl2 = _write_impl(os.path.join(tmpdir, "impl2.json"), "E1.S1.A2", [t2])

        cov_path = _make_covdb(tmpdir, [t1, t2])
        if cov_path is None:
            print("SKIP test_prove_refuses_mass_demote_of_all_proven_nodes (coverage not installed)")
            return

        green = os.path.join(tmpdir, "green.trx")
        with open(green, "w") as f:
            f.write(_make_trx({t1: "Passed", t2: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl1, "--impl", impl2,
                       "--results", green, "--coverage", cov_path])
        assert rc == 0, f"initial prove of both ACs must pass:\n{out}"
        lock = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert lock["nodes"]["engine::E1.S1.A1"]["status"] == "proven"
        assert lock["nodes"]["engine::E1.S1.A2"]["status"] == "proven"

        # Both bound tests now go red -- this run would demote EVERY currently-proven node.
        red = os.path.join(tmpdir, "red.trx")
        with open(red, "w") as f:
            f.write(_make_trx({t1: "Failed", t2: "Failed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl1, "--impl", impl2,
                       "--results", red, "--coverage", cov_path])
        assert rc != 0, f"mass-demote must be refused (non-zero exit):\n{out}"
        assert "REFUSING" in out, f"expected a loud refusal in output:\n{out}"
        assert "DEMOTED" not in out, f"must not silently demote:\n{out}"
        lock_after = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert lock_after["nodes"]["engine::E1.S1.A1"]["status"] == "proven", (
            "refusal must leave the lock untouched")
        assert lock_after["nodes"]["engine::E1.S1.A2"]["status"] == "proven", (
            "refusal must leave the lock untouched")

        # --allow-mass-demote overrides the refusal (both tests genuinely failed, so the AAA
        # still exits non-zero -- but now because of the failures, and it actually demotes).
        rc2, out2 = run(["--root", spec_dir, "prove", "--impl", impl1, "--impl", impl2,
                        "--results", red, "--coverage", cov_path, "--allow-mass-demote"])
        assert rc2 != 0, f"still non-zero (both bound tests genuinely failed):\n{out2}"
        assert "DEMOTED" in out2, f"override must let the demotion through:\n{out2}"
        lock_final = read_lock(os.path.join(spec_dir, "facit.lock.json"))
        assert lock_final["nodes"]["engine::E1.S1.A1"]["status"] == "unproven"
        assert lock_final["nodes"]["engine::E1.S1.A2"]["status"] == "unproven"
    print("PASS test_prove_refuses_mass_demote_of_all_proven_nodes")


def test_prove_records_test_sources_from_config_testroots():
    """DEFECT 2(a): with testRoots configured via facit.config.json (no --test-root CLI flag),
    prove records testSources for the proving test -- the config-driven path must actually work
    end-to-end, not just the --test-root flag."""
    with tempfile.TemporaryDirectory() as tmp:
        spec_dir = _make_minimal_facit(tmp)
        method = "my_configured_proving_test"
        tdir, tpath = _write_proving_test_file(tmp, method, "original")
        with open(os.path.join(spec_dir, "facit.config.json"), "w") as f:
            json.dump({"testRoots": [tdir]}, f)
        impl_path = _make_impl(tmp, spec_dir, [method])
        cov_path = _make_covdb(tmp, [method])
        if cov_path is None:
            print("SKIP test_prove_records_test_sources_from_config_testroots (coverage not installed)")
            return
        trx = os.path.join(tmp, "r.trx")
        with open(trx, "w") as f:
            f.write(_make_trx({method: "Passed"}))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx,
                       "--coverage", cov_path])
        assert rc == 0, f"prove must pass:\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        ts = node.get("testSources", [])
        assert ts and ts[0]["path"] == os.path.realpath(os.path.abspath(tpath)), (
            f"testSources must be populated from config-only testRoots (no --test-root flag): {node}")
    print("PASS test_prove_records_test_sources_from_config_testroots")


def test_conform_warns_on_proven_node_missing_test_sources():
    """DEFECT 2(b): conform's proving-test-drift check is inert (checks nothing) for a proven
    node that carries no testSources -- surfaced as a visible WARN instead of silently skipped.
    The warning must not affect the conformant/drifted/unverifiable counts or the exit code."""
    try:
        import coverage as coverage_mod  # noqa: F401
    except ImportError:
        print("SKIP test_conform_warns_on_proven_node_missing_test_sources (coverage not installed)")
        return
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        test_id = "NS.C.t_no_test_source"
        impl_path = _make_impl(tmpdir, spec_dir, [test_id])
        cov_path = _make_covdb(tmpdir, [test_id])
        trx_path = os.path.join(tmpdir, "r.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx({test_id: "Passed"}))
        # Prove WITHOUT --test-root and with no facit.config.json testRoots -> coveredFiles
        # populated, testSources absent (mirrors docs/facit's lock before this fix).
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path])
        assert rc == 0, out
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"]["engine::E1.S1.A1"]
        assert node.get("coveredFiles") and not node.get("testSources"), (
            f"fixture must have coveredFiles but no testSources: {node}")

        rc, out = run(["--root", spec_dir, "conform"])
        assert rc == 0, f"missing testSources alone must not fail conform:\n{out}"
        assert "0 drifted" in out and "0 unverifiable" in out, out
        assert "WARN" in out and "testSources" in out, f"expected a visible testSources warning:\n{out}"
        assert "WARN-NO-TEST-SOURCE" in out, out
    print("PASS test_conform_warns_on_proven_node_missing_test_sources")


def test_parse_trx_aggregates_theory_cases_by_worst_outcome():
    """DEFECT 2(c): TRX Theory fail-open. Multiple UnitTestResults sharing one FQN (an xUnit
    [Theory]'s parameterized cases all share the SAME TestMethod className+name -- parameters
    aren't part of the method identity) must aggregate by WORST outcome, not first-writer-wins.
    A Passed case recorded before a Failed case must not hide the failure."""
    m = _import_facit_module()
    with tempfile.TemporaryDirectory() as tmpdir:
        trx_path = os.path.join(tmpdir, "theory.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx_with_defs([
                {"id": "aaaa1111-0000-0000-0000-000000000001", "displayName": "MyTheory(x: 1)",
                 "className": "NS.MyClass", "methodName": "MyTheory", "outcome": "Passed"},
                {"id": "aaaa1111-0000-0000-0000-000000000002", "displayName": "MyTheory(x: 2)",
                 "className": "NS.MyClass", "methodName": "MyTheory", "outcome": "Failed"},
            ]))
        results = m._parse_trx(trx_path)
        assert results["NS.MyClass.MyTheory"] != "Passed", (
            f"a later Failed Theory case must not be hidden by an earlier Passed one "
            f"(first-writer-wins bug): {results}")
        assert results["NS.MyClass.MyTheory"] == "Failed", results
        # Each parameterized case's OWN display-name outcome must still be reported correctly.
        assert results["MyTheory(x: 1)"] == "Passed"
        assert results["MyTheory(x: 2)"] == "Failed"
    print("PASS test_parse_trx_aggregates_theory_cases_by_worst_outcome")


def test_prove_refuses_theory_binding_when_a_later_case_fails():
    """DEFECT 2(c) integration: an AC bound to an xUnit [Theory] method by its bare FQN must be
    REFUSED when even one parameterized case failed -- not silently proven because an earlier
    case happened to pass first in the TRX. Coverage is supplied so the refusal (pre-fix, a
    wrongly-succeeding prove; post-fix, a correct TEST-FAILED refusal) isn't masked by the
    separate strict NO-COVERAGE gate."""
    with tempfile.TemporaryDirectory() as tmpdir:
        spec_dir = _make_minimal_facit(tmpdir)
        binding = "NS.MyClass.MyTheory"
        impl_path = _make_impl(tmpdir, spec_dir, [binding])
        cov_path = _make_covdb(tmpdir, [binding])
        if cov_path is None:
            print("SKIP test_prove_refuses_theory_binding_when_a_later_case_fails (coverage not installed)")
            return
        trx_path = os.path.join(tmpdir, "theory.trx")
        with open(trx_path, "w") as f:
            f.write(_make_trx_with_defs([
                {"id": "bbbb2222-0000-0000-0000-000000000001", "displayName": "MyTheory(x: 1)",
                 "className": "NS.MyClass", "methodName": "MyTheory", "outcome": "Passed"},
                {"id": "bbbb2222-0000-0000-0000-000000000002", "displayName": "MyTheory(x: 2)",
                 "className": "NS.MyClass", "methodName": "MyTheory", "outcome": "Failed"},
            ]))
        rc, out = run(["--root", spec_dir, "prove", "--impl", impl_path, "--results", trx_path,
                       "--coverage", cov_path])
        assert rc != 0, f"a Theory binding with a failing later case must be refused, not proven:\n{out}"
        assert "TEST-FAILED" in out, f"expected a TEST-FAILED refusal (not NO-COVERAGE):\n{out}"
        node = read_lock(os.path.join(spec_dir, "facit.lock.json"))["nodes"].get("engine::E1.S1.A1")
        assert node is None or node["status"] != "proven", f"must not be proven: {node}"
    print("PASS test_prove_refuses_theory_binding_when_a_later_case_fails")


if __name__ == "__main__":
    tests = [
        test_compile_default_root,
        test_validate_default_root,
        test_compile_explicit_docs_root,
        test_compile_cli_spec,
        test_validate_cli_spec,
        test_lock_then_diff_clean,
        test_diff_single_ac_change,
        test_lock_refuses_invalid_facit,
        test_relock_carries_proven_forward,
        test_docs_lock_untouched,
        # TASK 1
        test_compiled_cli_facit_has_E1S8A1,
        # TASK 2
        test_prove_marks_ac_proven_when_test_passes,
        test_prove_refuses_when_test_failed,
        test_prove_refuses_proven_without_binding,
        test_prove_refuses_when_node_diff_dirty,
        # TASK 3
        test_impl_E1S1A1_has_proof_tests,
        # CLI spec-first catch-up — E1.S8.A5/A6/A7 (prove behaviors that drifted ahead of the facit)
        test_prove_spans_multiple_impls_and_results_files,
        test_prove_collapses_displayname_and_fqn_of_same_test,
        test_prove_refuses_genuine_ambiguous_match,
        # E1.S9 — code-drift conformance
        test_parse_coverage_perTest_from_coveragepy,
        test_prove_records_coveredFiles_from_coverage,
        test_conform_reports_conformant_then_drifted,
        test_conform_flags_unverifiable_when_no_coveredfiles,
        # E1.S10 — genuine-proof guard
        test_prove_rejects_binding_whose_test_misses_impl_coverage,
        test_prove_accepts_when_coverage_intersects_impl,
        test_prove_skips_coverage_check_without_perTest,
        test_prove_warns_on_high_test_share,
        # E1.S11.A1 — junit ingestion + in-process harness
        test_parse_junit_outcomes,
        test_prove_accepts_junit_results,
        test_inprocess_run_matches_subprocess,
        # NEW: compile/validate/integrity/lock/diff/status/gap/conform pinning tests
        test_compile_reports_counts_and_stable_hash,
        test_compile_exit_codes_valid_zero_invalid_nonzero,
        test_validate_invalid_prints_errors_nonzero,
        test_compile_flags_duplicate_id,
        test_compile_flags_non_parent_bound_ac,
        test_compile_warns_unknown_extends,
        test_compile_warns_unknown_recommended_utility,
        test_lock_writes_per_node_hash_status_tests_and_metadata,
        test_diff_lists_added_changed_and_removed,
        test_status_reports_counts,
        test_gap_no_file_lists_every_ac,
        test_gap_file_reports_coverage_and_flags_unknown,
        test_conform_incremental_only_drifted,
        test_coverage_gate_normalizes_absolute_vs_relative_paths,
        # E1.S11.A3 — self-verify invariant (proven CLI ACs are coverage-backed + conform-clean)
        test_selfverify_proven_cli_acs_are_coverage_backed_and_conform_clean,
        # E1.S12.A1/A2/A3 — reverify
        test_reverify_runs_tests_and_proves,
        test_reverify_incremental_scope_selects_only_affected,
    ]

    failed = []
    for t in tests:
        try:
            t()
        except Exception as ex:
            print(f"FAIL {t.__name__}: {ex}")
            failed.append(t.__name__)

    print()
    print(f"Results: {len(tests) - len(failed)}/{len(tests)} passed")
    if failed:
        print("FAILED:", ", ".join(failed))
        sys.exit(1)
    else:
        print("All tests passed.")
        sys.exit(0)
