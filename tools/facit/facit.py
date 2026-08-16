#!/usr/bin/env python3
"""facit — manage the structured facit (engine + utilities + domain + targets).

Compiles every facit.json under a facit root into ONE view keyed by globally-qualified
node ids, validates it, and supports locking/diffing the whole tree (so you re-prove only
what changed) and validating a gap/implementation map against it.

Commands:
  compile   discover + schema-validate every facit, check integrity, report (──out writes compiled.json)
  validate  compile as a pass/fail gate (exit 1 on any error)
  lock      snapshot the compiled whole → <root>/facit.lock.json (per-node content-hash)
  diff      compare the live compiled facit to the lock → added / changed / removed nodes
  status    coverage summary (nodes, in-lock, changed, proven)
  gap       validate gap.json + implementation.json against the compiled facit + report coverage

Global option:
  --root <dir>  Facit root directory (default: docs/facit, resolved relative to repo root).
                The lock file is <root>/facit.lock.json. The JSON Schema is always loaded from
                docs/facit/schema/facit.schema.json regardless of --root.

Node ids are local to each facit (E{n}.S{k}.A{m}); globally qualified as "<scopeRef>::<id>",
e.g. engine::E1.S1.A1, utility:crawl::E1.S2.A3. The lock is one file over all nodes, but a
change to one component only changes that component's node hashes.
"""
import argparse, glob, hashlib, json, os, re, shutil, subprocess, sys, tempfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

# Repo root: two levels up from this file (tools/facit/facit.py → repo root)
REPO_ROOT = os.path.realpath(os.path.join(os.path.dirname(__file__), "..", ".."))

# Default facit root — always docs/facit relative to repo root
DEFAULT_FACIT_DIR = os.path.join(REPO_ROOT, "docs", "facit")

# JSON Schema is always loaded from docs/facit/schema regardless of --root
SCHEMA_DIR = os.path.join(DEFAULT_FACIT_DIR, "schema")


def _load(path):
    with open(path) as f:
        return json.load(f)


def scope_ref(scope):
    lvl = scope["level"]
    if lvl == "app":
        return "app"
    if lvl == "engine":
        return "engine"
    if lvl == "domain":
        return f"domain:{scope['domain']}"
    if lvl == "target":
        return f"domain:{scope['domain']}/target:{scope['target']}"
    if lvl == "utility":
        return f"utility:{scope['utility']}"
    return f"unknown:{lvl}"


def _norm(s):
    return re.sub(r"\s+", " ", (s or "").strip())


def discover(facit_dir):
    return sorted(glob.glob(os.path.join(facit_dir, "**", "facit.json"), recursive=True))


def compile_facits(facit_dir):
    """Returns (nodes, errors, warnings). nodes = {qid: {kind, hash, scopeRef, localId, text}}."""
    errors, warnings, nodes = [], [], {}
    try:
        import jsonschema
        schema = _load(os.path.join(SCHEMA_DIR, "facit.schema.json"))
        validator = jsonschema.Draft202012Validator(schema)
    except ImportError:
        validator = None
        warnings.append("jsonschema not installed — schema validation skipped")

    facit_refs = set()
    utility_ids = set()
    facits = []
    for path in discover(facit_dir):
        try:
            fc = _load(path)
        except Exception as ex:
            errors.append(f"{path}: not valid JSON ({ex})")
            continue
        if validator is not None:
            for e in sorted(validator.iter_errors(fc), key=lambda e: e.path):
                errors.append(f"{path}: schema: {e.message} (at {'/'.join(map(str, e.path))})")
        ref = scope_ref(fc.get("scope", {"level": "?"}))
        facit_refs.add(ref)
        if fc.get("scope", {}).get("level") == "utility":
            utility_ids.add(fc["scope"]["utility"])
        facits.append((path, ref, fc))

    for path, ref, fc in facits:
        local_ids = set()
        for epic in fc.get("epics", []):
            for st in epic.get("stories", []):
                if st["id"] in local_ids:
                    errors.append(f"{ref}: duplicate id {st['id']}")
                local_ids.add(st["id"])
                qid = f"{ref}::{st['id']}"
                nodes[qid] = {
                    "kind": "story", "scopeRef": ref, "localId": st["id"],
                    "text": json.dumps({"role": _norm(st["role"]), "want": _norm(st["want"]),
                                        "soThat": _norm(st["soThat"])}, sort_keys=True),
                }
                for ac in st.get("acceptanceCriteria", []):
                    if ac["id"] in local_ids:
                        errors.append(f"{ref}: duplicate id {ac['id']}")
                    local_ids.add(ac["id"])
                    if not ac["id"].startswith(st["id"] + "."):
                        errors.append(f"{ref}: ac {ac['id']} not bound to story {st['id']}")
                    qac = f"{ref}::{ac['id']}"
                    nodes[qac] = {"kind": "ac", "scopeRef": ref, "localId": ac["id"],
                                  "text": _norm(ac["text"])}
        # integrity (warnings during build-out)
        ext = fc.get("extends")
        if ext not in (None, "engine") and ext not in facit_refs:
            warnings.append(f"{ref}: extends '{ext}' resolves to no known facit")
        uses = fc.get("uses", {})
        for u in uses.get("recommendedUtilities", []):
            if u not in utility_ids:
                warnings.append(f"{ref}: recommendedUtility '{u}' has no utility facit yet")

    for qid, n in nodes.items():
        n["hash"] = hashlib.sha256(n["text"].encode()).hexdigest()[:16]
    return nodes, errors, warnings


def facit_hash(nodes):
    blob = "\n".join(f"{qid}:{n['hash']}" for qid, n in sorted(nodes.items()))
    return hashlib.sha256(blob.encode()).hexdigest()[:16]


def _lock_path(facit_dir):
    return os.path.join(facit_dir, "facit.lock.json")


def cmd_compile(args):
    nodes, errors, warnings = compile_facits(args.root)
    stories = sum(1 for n in nodes.values() if n["kind"] == "story")
    acs = sum(1 for n in nodes.values() if n["kind"] == "ac")
    refs = sorted({n["scopeRef"] for n in nodes.values()})
    print(f"facits: {len(refs)}  ({', '.join(refs)})")
    print(f"nodes:  {len(nodes)}  (stories={stories}, acs={acs})")
    print(f"facitHash: {facit_hash(nodes)}")
    for w in warnings:
        print(f"  warn: {w}")
    for e in errors:
        print(f"  ERROR: {e}")
    if args.out:
        _write(args.out, {"facitHash": facit_hash(nodes), "nodes": nodes})
        print(f"wrote {args.out}")
    return 1 if errors else 0


def cmd_validate(args):
    nodes, errors, warnings = compile_facits(args.root)
    for w in warnings:
        print(f"warn: {w}")
    if errors:
        for e in errors:
            print(f"ERROR: {e}")
        print(f"INVALID ({len(errors)} errors)")
        return 1
    print(f"VALID — {len(nodes)} nodes, facitHash {facit_hash(nodes)}")
    return 0


def _write(path, obj):
    # Atomic durable write (E1.S5.A4): serialize to a temp file in the SAME directory,
    # then os.replace() onto the target — an atomic rename on POSIX. A concurrent reader
    # or an interrupted write therefore never observes a truncated/partial lock; the trust
    # anchor is never left corrupt. (The temp lives in the same dir so replace stays atomic
    # across the same filesystem.)
    d = os.path.dirname(os.path.abspath(path))
    os.makedirs(d, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=d, prefix=".facit-tmp-", suffix=".json")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(obj, f, indent=2, ensure_ascii=False)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def cmd_lock(args):
    lock_path = _lock_path(args.root)
    nodes, errors, warnings = compile_facits(args.root)
    if errors:
        print("refusing to lock an invalid facit:")
        for e in errors:
            print(f"  ERROR: {e}")
        return 1
    prev = _load(lock_path) if os.path.exists(lock_path) else {"nodes": {}}
    prev_nodes = prev.get("nodes", {})
    locked = {}
    for qid, n in nodes.items():
        old = prev_nodes.get(qid)
        # carry proven status/tests forward only if the content hash is unchanged
        if old and old.get("hash") == n["hash"]:
            locked[qid] = {"hash": n["hash"], "status": old.get("status", "unproven"),
                           "tests": old.get("tests", [])}
            # Carry forward the full proof — its code-drift AND test-drift triggers — for an
            # unchanged node; dropping either would silently blind conform/verify after a re-lock.
            if "coveredFiles" in old:
                locked[qid]["coveredFiles"] = old["coveredFiles"]
            if "testSources" in old:
                locked[qid]["testSources"] = old["testSources"]
        else:
            locked[qid] = {"hash": n["hash"], "status": "unproven", "tests": []}
    lock = {"schemaVersion": 1,
            "lockedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "facitHash": facit_hash(nodes), "nodes": locked}
    _write(lock_path, lock)
    proven = sum(1 for v in locked.values() if v["status"] == "proven")
    print(f"locked {len(locked)} nodes ({proven} proven) → {os.path.relpath(lock_path, REPO_ROOT)}")
    print(f"facitHash: {lock['facitHash']}")
    return 0


def cmd_diff(args):
    lock_path = _lock_path(args.root)
    nodes, errors, _ = compile_facits(args.root)
    if not os.path.exists(lock_path):
        print("no lock yet — run `facit lock` first")
        return 1
    lock = _load(lock_path)["nodes"]
    live, locked = set(nodes), set(lock)
    added = sorted(live - locked)
    removed = sorted(locked - live)
    changed = sorted(q for q in live & locked if nodes[q]["hash"] != lock[q]["hash"])
    for q in added:
        print(f"  + added    {q}")
    for q in removed:
        print(f"  - removed  {q}")
    for q in changed:
        print(f"  ~ changed  {q}")
    n = len(added) + len(removed) + len(changed)
    print(f"{n} node(s) changed since lock"
          + (f"  (added={len(added)} removed={len(removed)} changed={len(changed)})" if n else " — clean"))
    return 0


def cmd_status(args):
    lock_path = _lock_path(args.root)
    nodes, errors, _ = compile_facits(args.root)
    lock = _load(lock_path).get("nodes", {}) if os.path.exists(lock_path) else {}
    proven = sum(1 for q in nodes if lock.get(q, {}).get("status") == "proven"
                 and lock[q]["hash"] == nodes[q]["hash"])
    changed = sum(1 for q in nodes if q in lock and lock[q]["hash"] != nodes[q]["hash"])
    print(f"nodes: {len(nodes)}  locked: {len(lock)}  proven(current): {proven}  "
          f"changed-since-lock: {changed}  unlocked: {len(set(nodes) - set(lock))}")
    return 1 if errors else 0


def cmd_gap(args):
    nodes, errors, _ = compile_facits(args.root)
    ac_ids = {q for q, n in nodes.items() if n["kind"] == "ac"}
    if not args.file:
        # just list the AC ids so a gap can be built on the compiled whole
        for q in sorted(ac_ids):
            print(q)
        print(f"# {len(ac_ids)} acceptance criteria", file=sys.stderr)
        return 0
    gap = _load(args.file)
    seen = set()
    for item in gap.get("items", gap.get("entries", [])):
        seen.add(item["acId"])
    # acId here is local (E..) — match by local id across any scope
    local = {n["localId"] for q, n in nodes.items() if n["kind"] == "ac"}
    unknown = sorted(a for a in seen if a not in local)
    uncovered = sorted(local - seen)
    print(f"gap entries: {len(seen)}  acs: {len(local)}  uncovered: {len(uncovered)}  unknown-acId: {len(unknown)}")
    for a in unknown[:20]:
        print(f"  unknown acId: {a}")
    return 1 if unknown else 0


# Sentinel outcome: a bare display-name that maps to genuinely different tests with
# DIFFERING outcomes (E1.S8.A7). _match_trx raises on it so a binding by bare name fails
# closed rather than silently resolving to a last-writer-wins outcome.
_AMBIGUOUS = "__FACIT_AMBIGUOUS_MATCH__"


def _parse_trx(trx_path):
    """Parse a .trx file and return {name: outcome} dict.

    Keys include BOTH the test display-name (UnitTestResult/@testName) AND the
    fully-qualified method name (TestMethod/@className + '.' + TestMethod/@name)
    so that proof.tests entries can use either form.

    Handles both namespaced (http://microsoft.com/schemas/VisualStudio/TeamTest/2010)
    and un-namespaced elements, for compatibility with minimal test fixtures.
    """
    tree = ET.parse(trx_path)
    root_el = tree.getroot()
    ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

    def _iter(tag):
        """Yield elements by tag, trying namespaced form first."""
        found = list(root_el.iter(f"{{{ns}}}{tag}"))
        return found if found else list(root_el.iter(tag))

    # Step 1: testId (GUID) → outcome from Results
    guid_to_outcome = {}
    for el in _iter("UnitTestResult"):
        test_id_guid = el.get("testId", "")
        outcome = el.get("outcome", "")
        if test_id_guid and outcome:
            guid_to_outcome[test_id_guid] = outcome

    # Step 2: testId (GUID) → FQN method name from TestDefinitions
    guid_to_fqn = {}
    for el in _iter("UnitTest"):
        unit_id = el.get("id", "")
        # TestMethod child carries className and name (method name)
        for tm_tag in (f"{{{ns}}}TestMethod", "TestMethod"):
            tm = el.find(tm_tag)
            if tm is not None:
                class_name = tm.get("className", "")
                method_name = tm.get("name", "")
                if class_name and method_name:
                    guid_to_fqn[unit_id] = f"{class_name}.{method_name}"
                break

    # Step 3: Build results dict — display name + FQN method name both as keys.
    # E1.S8.A7: two GENUINELY different tests (distinct testIds) that share a bare display-name
    # must NOT collapse to a single last-writer-wins outcome. Track the outcomes seen per
    # display-name across distinct records; a display-name produced with DIFFERING outcomes is
    # recorded as an ambiguity sentinel so _match_trx fails closed on a bare-name binding.
    results = {}
    display_outcomes = {}  # display_name → set of outcomes seen
    fqn_outcomes = {}      # fqn → list of outcomes seen, in order
    for el in _iter("UnitTestResult"):
        test_id_guid = el.get("testId", "")
        display_name = el.get("testName", "")
        outcome = el.get("outcome", "")
        if not outcome:
            continue
        if display_name:
            display_outcomes.setdefault(display_name, set()).add(outcome)
            results[display_name] = outcome
        fqn = guid_to_fqn.get(test_id_guid)
        if fqn:
            fqn_outcomes.setdefault(fqn, []).append(outcome)
    for _dname, _outs in display_outcomes.items():
        if len(_outs) > 1:
            results[_dname] = _AMBIGUOUS

    # Worst-outcome aggregation per FQN (fixes TRX Theory fail-open): an xUnit [Theory] gives
    # every parameterized case its OWN testId/UnitTestResult but the SAME TestMethod
    # className+name (parameters aren't part of the method identity) — so several distinct
    # cases share one FQN key here. First-writer-wins would let an early Passed case hide a
    # later Failed one; aggregate instead: any non-Passed case among them makes the FQN
    # not-passed. Skip a fqn string already claimed by an exact display-name key above (do not
    # clobber that binding).
    for _fqn, _outs in fqn_outcomes.items():
        if _fqn in results:
            continue
        _non_passed = [o for o in _outs if o != "Passed"]
        results[_fqn] = _non_passed[0] if _non_passed else _outs[0]

    # Fallback for un-namespaced minimal fixtures without TestDefinitions
    if not results:
        for el in root_el.iter("UnitTestResult"):
            name = el.get("testName", "")
            outcome = el.get("outcome", "")
            if name and outcome:
                results[name] = outcome

    return results


def _parse_junit(path):
    """Parse a JUnit XML (pytest --junitxml) and return {name: outcome} dict.

    Handles both <testsuites> (root) and <testsuite> (root) forms.
    For each <testcase classname="..." name="test_x">:
      - Outcome = "Failed"  if a <failure> or <error> child is present
      - Outcome = "Skipped" if a <skipped> child is present
      - Outcome = "Passed"  otherwise
    Keys: BOTH the bare name ("test_x") AND the classname.name form ("classname.test_x"),
    so _match_trx resolves either form used in proof.tests bindings.
    """
    tree = ET.parse(path)
    root_el = tree.getroot()
    results = {}
    name_outcomes = {}  # bare name → set of outcomes (E1.S8.A7 fail-closed)

    def _process_testcase(tc_el):
        classname = tc_el.get("classname", "")
        name = tc_el.get("name", "")
        if not name:
            return
        # Determine outcome from child elements
        if tc_el.find("failure") is not None or tc_el.find("error") is not None:
            outcome = "Failed"
        elif tc_el.find("skipped") is not None:
            outcome = "Skipped"
        else:
            outcome = "Passed"
        # Bare name key (always present)
        name_outcomes.setdefault(name, set()).add(outcome)
        results[name] = outcome
        # classname.name key (when classname is non-empty)
        if classname:
            fqn = f"{classname}.{name}"
            if fqn not in results:
                results[fqn] = outcome

    # Strip namespace from the root tag for reliable matching
    root_tag = root_el.tag
    if "}" in root_tag:
        root_tag = root_tag.split("}", 1)[1]

    if root_tag == "testsuites":
        for tc in root_el.iter("testcase"):
            _process_testcase(tc)
    elif root_tag == "testsuite":
        for tc in root_el.iter("testcase"):
            _process_testcase(tc)
    else:
        # Fallback: search for testcase anywhere in the document
        for tc in root_el.iter("testcase"):
            _process_testcase(tc)

    for _n, _outs in name_outcomes.items():
        if len(_outs) > 1:
            results[_n] = _AMBIGUOUS

    return results


def _parse_results(path):
    """Detect test result format and dispatch to the right parser.

    Detects by XML root element tag (namespace-stripped):
      - "TestRun"               → VSTest TRX  (_parse_trx)
      - "testsuite"/"testsuites" → JUnit XML   (_parse_junit)
      - anything else           → try TRX first, then JUnit
    """
    tree = ET.parse(path)
    root_el = tree.getroot()
    tag = root_el.tag
    if "}" in tag:
        tag = tag.split("}", 1)[1]

    if tag == "TestRun":
        return _parse_trx(path)
    if tag in ("testsuite", "testsuites"):
        return _parse_junit(path)
    # Unknown format — try TRX, fall back to JUnit
    try:
        return _parse_trx(path)
    except Exception:
        return _parse_junit(path)


def _scope_for_impl(impl_path):
    """Derive the scope ref for an implementation.json by reading the adjacent facit.json."""
    impl_dir = os.path.dirname(os.path.abspath(impl_path))
    facit_path = os.path.join(impl_dir, "facit.json")
    if not os.path.exists(facit_path):
        return None
    try:
        fc = _load(facit_path)
    except Exception:
        return None
    return scope_ref(fc.get("scope", {"level": "?"}))


def _match_trx(test_id, trx_results):
    """Find the outcome of test_id in trx_results.

    Matching strategy (in order):
      1. Exact key match.
      2. The test_id is a suffix of some key (handles FQN stored as shorter name).
      3. Some key is a suffix of test_id.
    Returns (outcome, matched_key) or (None, None) if zero matches.
    Raises ValueError on multiple matches (ambiguous).
    """
    # 1. Exact key match wins outright (the FQN or display-name is recorded verbatim).
    if test_id in trx_results:
        if trx_results[test_id] == _AMBIGUOUS:
            raise ValueError(
                f"Ambiguous match for '{test_id}': that bare display-name maps to genuinely "
                f"different tests with differing outcomes — bind by fully-qualified name")
        return trx_results[test_id], test_id
    # 2. Suffix matches — handles a FQN bound against a shorter recorded name or vice versa.
    matches = {k: o for k, o in trx_results.items()
               if k.endswith("." + test_id) or test_id.endswith("." + k)}
    if not matches:
        return None, None
    if any(o == _AMBIGUOUS for o in matches.values()):
        raise ValueError(
            f"Ambiguous match for '{test_id}': a shared bare display-name is ambiguous "
            f"(different tests, differing outcomes) — bind by fully-qualified name")
    if len(matches) > 1:
        # The TRX records both a display-name key and an FQN key for the SAME test when the
        # method has no custom DisplayName. If every match is the same outcome, it is one test,
        # not a real ambiguity — take the most-specific (longest) key.
        if len(set(matches.values())) == 1:
            key = max(matches, key=len)
            return matches[key], key
        raise ValueError(f"Ambiguous match for '{test_id}': {list(matches.keys())}")
    key = next(iter(matches))
    return matches[key], key


def _parse_coverage(path):
    """Parse a coverage file and return {"perTest": {ctx: set(files)} or None, "files": set(files)}.

    Supports:
    - coverage.py data file (*.coverage or any non-xml path): per-test context mapping available.
    - cobertura XML (*.xml): aggregate only, perTest=None.

    File paths are returned raw (absolute for coverage.py, as recorded for cobertura).
    """
    path = os.path.abspath(path)
    ext = os.path.splitext(path)[1].lower()

    if ext == ".xml":
        # Cobertura XML — aggregate coverage only
        tree = ET.parse(path)
        root_el = tree.getroot()
        sources = []
        for src in root_el.iter("source"):
            if src.text:
                sources.append(src.text.strip())
        files = set()
        for cls_el in root_el.iter("class"):
            fname = cls_el.get("filename")
            if not fname:
                continue
            has_hits = any(int(line.get("hits", 0)) > 0 for line in cls_el.iter("line"))
            if not has_hits:
                continue
            if not os.path.isabs(fname) and sources:
                for src in sources:
                    candidate = os.path.join(src, fname)
                    if os.path.exists(candidate):
                        fname = candidate
                        break
            files.add(fname)
        return {"perTest": None, "files": files}

    else:
        # coverage.py sqlite data file
        try:
            import coverage as coverage_mod
        except ImportError:
            raise ImportError(
                f"The 'coverage' package is required to read '{os.path.basename(path)}'. "
                "Install it with: pip install coverage"
            )
        cd = coverage_mod.CoverageData(basename=path)
        cd.read()
        raw_files = set(cd.measured_files())
        per_test = {}
        for f in raw_files:
            ctxs_by_line = cd.contexts_by_lineno(f) or {}
            for _lineno, ctxs in ctxs_by_line.items():
                for ctx in ctxs:
                    if ctx:  # '' == no-context line; skip
                        per_test.setdefault(ctx, set()).add(f)
        return {
            "perTest": per_test if per_test else None,
            "files": raw_files,
        }


def _rel_to_repo(p):
    """Normalize a path to repo-relative (or keep absolute if outside the repo) so declared
    impl refs (repo-relative) and coverage paths (absolute) can be compared apples-to-apples."""
    ap = os.path.realpath(os.path.abspath(p))
    r = os.path.relpath(ap, REPO_ROOT)
    return ap if r.startswith("..") else r


def _compute_covered_files(test_ids, impl_entry, cov_per_test, cov_files, src_root):
    """Compute the coveredFiles list for a proven AC.

    - If perTest coverage is available: union files for every context whose name contains
      any of the test_ids (substring match, mirrors the spirit of _match_trx).
    - If only aggregate files: intersect declared evidence[kind=code] paths with cov_files.
    - Restrict to files under src_root; skip missing files.
    - Return sorted list of {"path": <repo-relative-or-abs>, "hash": <sha256[:16]>}.
    """
    src_root_abs = os.path.realpath(os.path.abspath(src_root))

    if cov_per_test is not None:
        # Per-test path: find coverage contexts whose name contains any test_id
        covered = set()
        for test_id in test_ids:
            for ctx, files in cov_per_test.items():
                if test_id in ctx or ctx in test_id:
                    covered.update(files)
    else:
        # Aggregate path (e.g. .NET cobertura — no per-test contexts).
        decl_code = [ev.get("ref", "").split(":")[0]
                     for ev in impl_entry.get("evidence", []) if ev.get("kind") == "code"]
        if decl_code:
            # Intersect declared impl code files with covered files — normalizing both to
            # repo-relative first (declared refs are repo-relative; coverage paths are absolute),
            # then keep the actual covered paths that match.
            decl_rel = {_rel_to_repo(d) for d in decl_code}
            covered = {f for f in cov_files if _rel_to_repo(f) in decl_rel}
        else:
            # E1.S9.A6: no declared code evidence → record every covered source file under
            # src-root as the trigger (the result loop below filters to src_root + existence),
            # so a coverage-backed proof is always populated even without hand-declared evidence.
            covered = set(cov_files)

    result = []
    for f in sorted(covered):
        abs_f = os.path.realpath(os.path.abspath(f))
        # Must be under src_root
        if not (abs_f == src_root_abs or abs_f.startswith(src_root_abs + os.sep)):
            continue
        if not os.path.exists(abs_f):
            continue  # skip files that don't exist on disk
        # Store repo-relative when possible, else absolute
        try:
            rel = os.path.relpath(abs_f, REPO_ROOT)
            stored_path = abs_f if rel.startswith("..") else rel
        except ValueError:
            stored_path = abs_f
        with open(abs_f, "rb") as fh:
            file_hash = hashlib.sha256(fh.read()).hexdigest()[:16]
        result.append({"path": stored_path, "hash": file_hash})

    return result


def _hash_declared_code(impl_entry, src_root):
    """E1.S9.A7: hash the declared code-evidence files (the GOVERNED code) directly — the
    code-drift trigger for a STRUCTURAL proof (a reflection/source-inspection test that executes
    no product code, so coverage cannot attribute a trigger). Returns coveredFiles-shaped entries
    for declared code files that exist under src_root."""
    src_root_abs = os.path.realpath(os.path.abspath(src_root))
    result = []
    for ev in impl_entry.get("evidence", []):
        if ev.get("kind") != "code":
            continue
        f = ev.get("ref", "").split(":")[0]
        abs_f = os.path.realpath(os.path.abspath(f))
        if not (abs_f == src_root_abs or abs_f.startswith(src_root_abs + os.sep)):
            continue
        if not os.path.exists(abs_f):
            continue
        rel = os.path.relpath(abs_f, REPO_ROOT)
        stored = abs_f if rel.startswith("..") else rel
        with open(abs_f, "rb") as fh:
            h = hashlib.sha256(fh.read()).hexdigest()[:16]
        result.append({"path": stored, "hash": h})
    return result


def _read_facit_config(root):
    """Read optional <root>/facit.config.json (testRoots, testCommand). Returns {} if absent."""
    cfg_path = os.path.join(root, "facit.config.json")
    if os.path.exists(cfg_path):
        try:
            return _load(cfg_path)
        except Exception:
            return {}
    return {}


def _find_method_span(lines, method, is_python):
    """Return (start_line, end_line) 1-based inclusive of `method`'s source, or None.

    Python: from the `def <method>(` line to the last line before the next line at an indent
    <= the def's indent (dedent), trailing blanks trimmed.
    C#: from the declaration line (method name followed by '(', not a '.'-prefixed call) to the
    matching closing brace of its body.
    """
    if is_python:
        pat = re.compile(r'^(\s*)(?:async\s+)?def\s+' + re.escape(method) + r'\s*\(')
        for i, line in enumerate(lines):
            m = pat.match(line)
            if not m:
                continue
            indent = len(m.group(1))
            start = i + 1
            end = len(lines)
            for j in range(i + 1, len(lines)):
                s = lines[j]
                if s.strip() == "":
                    continue
                if (len(s) - len(s.lstrip())) <= indent:
                    end = j            # 1-based inclusive end = j (line index j is line j+1; body is up to line j)
                    break
            while end > start and lines[end - 1].strip() == "":
                end -= 1
            return (start, end)
        return None
    # C#
    decl = re.compile(r'(?<![\w.])' + re.escape(method) + r'\s*\(')
    for i, line in enumerate(lines):
        if not decl.search(line):
            continue
        depth = 0
        started = False
        for j in range(i, len(lines)):
            for ch in _strip_csharp_string_and_char_literals(lines[j]):
                if ch == '{':
                    depth += 1
                    started = True
                elif ch == '}':
                    depth -= 1
            if started and depth <= 0:
                return (i + 1, j + 1)
        return None
    return None


def _strip_csharp_string_and_char_literals(line):
    """Blank out the CONTENTS of C# string/char literals on a single line so brace-counting
    in _find_method_span isn't confused by a literal '{' or '}' inside an assertion string
    (this codebase constantly asserts against generated TS/JS/Python source snippets, which
    routinely contain an unmatched brace character, e.g. `Contain("void {")`). Handles regular
    "..." (backslash-escaped), verbatim/interpolated-verbatim @"..." ("" = escaped quote), and
    '.' char literals. Best-effort / single-line only (a multi-line verbatim string literal is
    rare in this codebase's assertions and, if present, only widens the scanned span — it does
    not silently narrow it, so it fails safe).
    """
    out = []
    i, n = 0, len(line)
    while i < n:
        ch = line[i]
        if ch == '@' and i + 1 < n and line[i + 1] == '"':
            out.append('@"')
            i += 2
            closed = False
            while i < n:
                if line[i] == '"':
                    if i + 1 < n and line[i + 1] == '"':
                        i += 2
                        continue
                    out.append('"')
                    i += 1
                    closed = True
                    break
                i += 1
            if not closed:
                break
            continue
        if ch == '"':
            out.append('"')
            i += 1
            closed = False
            while i < n:
                if line[i] == '\\' and i + 1 < n:
                    i += 2
                    continue
                if line[i] == '"':
                    out.append('"')
                    i += 1
                    closed = True
                    break
                i += 1
            if not closed:
                break
            continue
        if ch == "'":
            out.append("'")
            i += 1
            closed = False
            while i < n:
                if line[i] == '\\' and i + 1 < n:
                    i += 2
                    continue
                if line[i] == "'":
                    out.append("'")
                    i += 1
                    closed = True
                    break
                i += 1
            if not closed:
                break
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def _locate_test_source(test_id, test_roots):
    """Find a proving test's own source. Returns {test, path, startLine, endLine, hash} or None.
    test_id may be bare (method) or an FQN (…Class.method); matched by the LAST dotted segment."""
    method = test_id.split(".")[-1]
    for root in test_roots:
        root_abs = root if os.path.isabs(root) else os.path.join(REPO_ROOT, root)
        if not os.path.isdir(root_abs):
            continue
        for dirpath, _dirs, files in os.walk(root_abs):
            for fn in sorted(files):
                if not fn.endswith((".cs", ".py")):
                    continue
                fpath = os.path.join(dirpath, fn)
                try:
                    with open(fpath, "r", encoding="utf-8", errors="replace") as fh:
                        lines = fh.read().splitlines()
                except OSError:
                    continue
                span = _find_method_span(lines, method, fn.endswith(".py"))
                if span:
                    start, end = span
                    body = "\n".join(lines[start - 1:end])
                    h = hashlib.sha256(body.encode("utf-8")).hexdigest()[:16]
                    # Same convention as coveredFiles: repo-relative when possible, else
                    # absolute — an absolute path for an in-repo test would bind the lock
                    # to the machine/worktree that ran prove.
                    abs_f = os.path.realpath(os.path.abspath(fpath))
                    try:
                        rel = os.path.relpath(abs_f, REPO_ROOT)
                        stored_path = abs_f if rel.startswith("..") else rel
                    except ValueError:
                        stored_path = abs_f
                    return {"test": test_id, "path": stored_path,
                            "startLine": start, "endLine": end, "hash": h}
    return None


def _compute_test_sources(test_ids, test_roots):
    """Locate + hash each proving test's source (E1.S12.A4). Returns a list (may be empty)."""
    out = []
    for tid in test_ids:
        loc = _locate_test_source(tid, test_roots)
        if loc:
            out.append(loc)
    return out


def _test_source_hash_now(ts):
    """Recompute the hash of a recorded testSource's line range. Returns hash or None if gone."""
    path = ts["path"]
    if not os.path.isabs(path):  # canonical form is repo-relative; absolute = legacy locks
        path = os.path.join(REPO_ROOT, path)
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        lines = fh.read().splitlines()
    start, end = ts.get("startLine", 1), ts.get("endLine", len(lines))
    if end > len(lines):
        return None
    body = "\n".join(lines[start - 1:end])
    return hashlib.sha256(body.encode("utf-8")).hexdigest()[:16]


def cmd_conform(args):
    """Check code drift: recompute hashes of covered files for all proven lock nodes."""
    lock_path = _lock_path(args.root)
    if not os.path.exists(lock_path):
        print("no lock yet — run `facit lock` first")
        return 1
    lock_data = _load(lock_path)
    lock_nodes = lock_data.get("nodes", {})

    conformant = []
    drifted = []      # list of (qid, [(path, reason)])
    unverifiable = []
    no_test_sources = []  # proven nodes with coveredFiles but no testSources (E1.S12.A5/A6
                           # drift-check is inert for these — surfaced as a warning, not a fail)

    for qid in sorted(lock_nodes):
        node = lock_nodes[qid]
        if node.get("status") != "proven":
            continue
        covered = node.get("coveredFiles", [])
        if not covered:
            unverifiable.append(qid)
            continue
        if not node.get("testSources"):
            no_test_sources.append(qid)

        drifted_files = []
        for cf in covered:
            path = cf["path"]
            stored_hash = cf["hash"]
            abs_path = path if os.path.isabs(path) else os.path.normpath(
                os.path.join(REPO_ROOT, path))
            if not os.path.exists(abs_path):
                drifted_files.append((path, "missing"))
            else:
                with open(abs_path, "rb") as fh:
                    current_hash = hashlib.sha256(fh.read()).hexdigest()[:16]
                if current_hash != stored_hash:
                    drifted_files.append((path, f"hash {stored_hash} → {current_hash}"))

        # E1.S12.A5/A6: proving-test source drift — a changed/weakened test re-opens its AC
        # for re-run AND re-review (the tool cannot judge test correctness).
        for ts in node.get("testSources", []):
            now = _test_source_hash_now(ts)
            if now is None:
                drifted_files.append((ts["path"], f"test source missing (test '{ts.get('test')}')"))
            elif now != ts.get("hash"):
                drifted_files.append((ts["path"],
                    f"test '{ts.get('test')}' source changed {ts.get('hash')} → {now} — re-run + re-review"))

        if drifted_files:
            drifted.append((qid, drifted_files))
        else:
            conformant.append(qid)

    print(f"conform: {len(conformant)} conformant, {len(drifted)} drifted, "
          f"{len(unverifiable)} unverifiable")
    for qid, files in drifted:
        print(f"  DRIFTED: {qid}")
        for path, reason in files:
            print(f"    {path}: {reason}")
    for qid in unverifiable:
        print(f"  UNVERIFIABLE: {qid}")

    # E1.S12.A5/A6 drift-check is inert (checks nothing) for a proven node with no testSources —
    # make that visible instead of silently skipping it. Informational: does NOT affect the exit
    # code (a node missing testSources is not itself drift; re-run `prove` with testRoots
    # configured to populate it).
    if no_test_sources:
        print(f"  WARN: {len(no_test_sources)} proven node(s) have no testSources recorded — "
              f"proving-test drift cannot be detected for them (re-run `prove` with testRoots "
              f"configured in facit.config.json):")
        for qid in no_test_sources:
            print(f"    WARN-NO-TEST-SOURCE: {qid}")

    # E1.S9.A3 (strict): a proven node with no code-drift trigger cannot be trusted — fail.
    return 1 if (drifted or unverifiable) else 0


def _resolve_src_root(args):
    """Resolve the source root that bounds covered-file drift triggers (E1.S9.A8):
    an explicit --src-root wins; else the facit config's srcRoot (a relative path
    resolves against the repo root); else the facit root's parent — the default,
    which suits a self-contained project whose facit sits at the repo root, and
    which a facit nested below the repo (e.g. docs/facit) overrides via config."""
    if args.src_root:
        return os.path.abspath(args.src_root)
    cfg_src = _read_facit_config(args.root).get("srcRoot")
    if cfg_src:
        return cfg_src if os.path.isabs(cfg_src) else os.path.normpath(os.path.join(REPO_ROOT, cfg_src))
    return os.path.abspath(os.path.dirname(args.root))


def cmd_prove(args):
    """Prove ACs from implementation files against TRX test results."""
    lock_path = _lock_path(args.root)
    nodes, errors, _ = compile_facits(args.root)
    if errors:
        print("facit has errors — fix before proving:")
        for e in errors:
            print(f"  ERROR: {e}")
        return 1

    if not os.path.exists(lock_path):
        print("no lock yet — run `facit lock` first")
        return 1
    lock_data = _load(lock_path)
    lock_nodes = lock_data.get("nodes", {})

    # ── --verify path ────────────────────────────────────────────────────────
    if args.verify:
        # E1.S8.A9: if results are supplied, verify re-checks tests-green-NOW (not just that a
        # binding exists). Parse them once up front.
        verify_results = {}
        if args.results:
            for trx_path in args.results:
                if not os.path.exists(trx_path):
                    print(f"--results file not found: {trx_path}")
                    return 1
                try:
                    for k, v in _parse_results(trx_path).items():
                        verify_results[k] = v
                except Exception as ex:
                    print(f"Failed to parse results file {trx_path}: {ex}")
                    return 1
        failures = []
        for qid, node in lock_nodes.items():
            if node.get("status") != "proven":
                continue
            tests = node.get("tests", [])
            if not tests:
                failures.append(f"  PROVEN-WITHOUT-BINDING: {qid} has no test binding")
                continue
            # Check hash is still current (spec-clean)
            live = nodes.get(qid)
            if live is None or live["hash"] != node["hash"]:
                failures.append(f"  PROVEN-DIFF-DIRTY: {qid} node changed since lock (hash mismatch)")
            # Strict (E1.S9.A3): a proven node must carry a code-drift trigger.
            if not node.get("coveredFiles"):
                failures.append(f"  PROVEN-WITHOUT-COVERAGE: {qid} has no coveredFiles code-drift trigger")
            # E1.S8.A9: with results, the bound test must still be Passed NOW.
            if args.results:
                for test_id in tests:
                    try:
                        outcome, _mk = _match_trx(test_id, verify_results)
                    except ValueError as ex:
                        failures.append(f"  AMBIGUOUS-MATCH: {qid} test '{test_id}': {ex}")
                        continue
                    if outcome is None:
                        failures.append(f"  TEST-ABSENT-NOW: {qid} bound test '{test_id}' not in results (no longer green)")
                    elif outcome != "Passed":
                        failures.append(f"  TEST-NOT-GREEN-NOW: {qid} bound test '{test_id}' outcome='{outcome}' (expected Passed)")
        if failures:
            print("prove --verify FAILED:")
            for f in failures:
                print(f)
            return 1
        proven_count = sum(1 for n in lock_nodes.values() if n.get("status") == "proven")
        suffix = ", coverage-backed, and tests-green-now" if args.results else " and coverage-backed"
        print(f"prove --verify OK — {proven_count} proven node(s) all have bindings, are hash-clean{suffix}")
        return 0

    # ── normal prove path ────────────────────────────────────────────────────
    if not args.impl:
        print("--impl is required (repeatable: --impl path/to/implementation.json)")
        return 1
    if not args.results:
        print("--results is required (path to .trx file)")
        return 1

    # Parse coverage files if --coverage supplied
    cov_per_test = None   # merged {ctx: set(files)} across all coverage files
    cov_files = set()     # merged aggregate file set
    src_root = _resolve_src_root(args)
    if args.coverage:
        for cov_path in args.coverage:
            if not os.path.exists(cov_path):
                print(f"--coverage file not found: {cov_path}")
                return 1
            try:
                cov_data = _parse_coverage(cov_path)
            except Exception as ex:
                print(f"Failed to parse coverage file {cov_path}: {ex}")
                return 1
            if cov_data["perTest"] is not None:
                if cov_per_test is None:
                    cov_per_test = {}
                for ctx, files in cov_data["perTest"].items():
                    cov_per_test.setdefault(ctx, set()).update(files)
            cov_files.update(cov_data["files"])

    # Parse results files (TRX or JUnit; one or more; merged — proofs may span test projects)
    trx_results = {}
    for trx_path in args.results:
        if not os.path.exists(trx_path):
            print(f"--results file not found: {trx_path}")
            return 1
        try:
            for k, v in _parse_results(trx_path).items():
                trx_results[k] = v
        except Exception as ex:
            print(f"Failed to parse results file {trx_path}: {ex}")
            return 1

    # Build localId→qid mapping for this root
    local_to_qids = {}
    for qid, node in nodes.items():
        if node["kind"] == "ac":
            lid = node["localId"]
            local_to_qids.setdefault(lid, []).append(qid)

    # E1.S10.A2 — pre-pass: count ACs per test id for inflation warning
    test_ac_shares = {}  # test_id → number of ACs bound to it
    for _ip in args.impl:
        _ip_abs = os.path.abspath(_ip)
        if os.path.exists(_ip_abs):
            _impl_data = _load(_ip_abs)
            for _entry in _impl_data.get("entries", []):
                _proof = _entry.get("proof")
                if _proof:
                    for _tid in _proof.get("tests", []):
                        test_ac_shares[_tid] = test_ac_shares.get(_tid, 0) + 1

    # E1.S12.A4: test-source roots (from <root>/facit.config.json + --test-root) for hashing
    # each proving test's own source.
    cfg = _read_facit_config(args.root)
    test_roots = list(cfg.get("testRoots", []))
    if getattr(args, "test_root", None):
        test_roots += args.test_root

    failures = []
    proven_updates = {}  # qid → (test_ids, impl_entry, coveredFiles, testSources)
    demotions = set()    # qids whose bound test is now red/absent/uncovered (E1.S8.A8)

    for impl_path in args.impl:
        impl_path = os.path.abspath(impl_path)
        if not os.path.exists(impl_path):
            print(f"--impl file not found: {impl_path}")
            return 1

        impl_scope = _scope_for_impl(impl_path)
        impl_data = _load(impl_path)

        for entry in impl_data.get("entries", []):
            proof = entry.get("proof")
            if not proof:
                continue
            test_ids = proof.get("tests", [])
            if not test_ids:
                continue

            ac_id = entry["acId"]  # local id, e.g. "E1.S1.A1"

            # Resolve to qid: prefer scope-qualified if adjacent facit.json found
            if impl_scope:
                candidate_qid = f"{impl_scope}::{ac_id}"
                if candidate_qid not in nodes:
                    failures.append(f"  UNKNOWN-AC: {candidate_qid} not found in compiled facit")
                    continue
                qid = candidate_qid
            else:
                # Fall back to unscoped search: require exactly one match
                matches = local_to_qids.get(ac_id, [])
                if len(matches) == 0:
                    failures.append(f"  UNKNOWN-AC: {ac_id} not found in compiled facit")
                    continue
                if len(matches) > 1:
                    failures.append(
                        f"  AMBIGUOUS-AC: {ac_id} matches multiple scopes: "
                        f"{matches} — cannot prove without adjacent facit.json"
                    )
                    continue
                qid = matches[0]

            # Require lock entry exists (node must have been locked)
            lock_node = lock_nodes.get(qid)
            if lock_node is None:
                failures.append(f"  NOT-LOCKED: {qid} has no lock entry — run `facit lock` first")
                continue

            # Require diff-clean: live hash == lock hash
            live_node = nodes.get(qid)
            if live_node is None:
                failures.append(f"  MISSING-NODE: {qid} no longer in compiled facit")
                continue
            if live_node["hash"] != lock_node["hash"]:
                failures.append(
                    f"  DIFF-DIRTY: {qid} facit node changed since lock "
                    f"(lock={lock_node['hash']} live={live_node['hash']}) — re-lock first"
                )
                continue

            # Require every proof test id to match EXACTLY ONE trx result with outcome=Passed
            all_passed = True
            for test_id in test_ids:
                try:
                    outcome, matched_key = _match_trx(test_id, trx_results)
                except ValueError as ex:
                    failures.append(f"  AMBIGUOUS-MATCH for {qid}: {ex}")
                    all_passed = False
                    continue
                if outcome is None:
                    failures.append(
                        f"  TEST-ABSENT: {qid} proof test '{test_id}' "
                        f"not found in TRX (fail closed)"
                    )
                    all_passed = False
                elif outcome != "Passed":
                    failures.append(
                        f"  TEST-FAILED: {qid} proof test '{matched_key}' "
                        f"outcome='{outcome}' (expected Passed)"
                    )
                    all_passed = False

            if all_passed and (entry.get("proof") or {}).get("structural"):
                # E1.S9.A7 — STRUCTURAL proof: trigger is the declared governed code, hashed
                # directly (no runtime coverage). Must declare ≥1 governed code file.
                covered = _hash_declared_code(entry, src_root)
                if not covered:
                    failures.append(
                        f"  STRUCTURAL-NO-GOVERNED-FILE: {qid} — a structural proof must declare "
                        f"≥1 governed code file (evidence kind=code, under src-root)")
                    if lock_node.get("status") == "proven":
                        demotions.add(qid)
                else:
                    tsources = _compute_test_sources(test_ids, test_roots) if test_roots else []
                    proven_updates[qid] = (test_ids, entry, covered, tsources)
            elif all_passed:
                # E1.S10.A1 — coverage-intersection gate (per-AC, independent of others)
                # Only active when: --coverage supplied AND perTest data available AND
                # the impl entry declares ≥1 code evidence file.  Aggregate-only cobertura
                # (cov_per_test is None) cannot attribute coverage per-test, so the check
                # is skipped (cannot verify intersection → do not refuse).
                coverage_miss = False
                if args.coverage and cov_per_test is not None:
                    decl_files = [
                        ev.get("ref", "").split(":")[0]
                        for ev in entry.get("evidence", [])
                        if ev.get("kind") == "code"
                    ]
                    if decl_files:
                        # Gather files covered by this AC's proving tests
                        # (same context-substring matching as _compute_covered_files)
                        covered_for_tests = set()
                        for tid in test_ids:
                            for ctx, ctx_files in cov_per_test.items():
                                if tid in ctx or ctx in tid:
                                    covered_for_tests.update(ctx_files)
                        # Normalise both sides to repo-relative before intersecting:
                        # coverage reports ABSOLUTE paths, declared impl refs are repo-relative.
                        def _rel(p):
                            ap = os.path.realpath(os.path.abspath(p))
                            r = os.path.relpath(ap, REPO_ROOT)
                            return ap if r.startswith("..") else r
                        covered_rel = {_rel(f) for f in covered_for_tests}
                        decl_rel = {_rel(d) for d in decl_files}
                        if not covered_rel.intersection(decl_rel):
                            failures.append(
                                f"  COVERAGE-MISS: {qid} — proving test(s) {test_ids} "
                                f"do not execute the AC's implementation {decl_files}"
                            )
                            coverage_miss = True
                # E1.S9.A5 (strict): a proof must carry a populated code-drift trigger.
                covered = (_compute_covered_files(test_ids, entry, cov_per_test, cov_files, src_root)
                           if args.coverage else [])
                if coverage_miss:
                    if lock_node.get("status") == "proven":
                        demotions.add(qid)
                elif not covered:
                    failures.append(
                        f"  NO-COVERAGE: {qid} — cannot prove without a populated coveredFiles "
                        f"code-drift trigger (supply --coverage that executes the proving test)")
                    if lock_node.get("status") == "proven":
                        demotions.add(qid)
                else:
                    tsources = _compute_test_sources(test_ids, test_roots) if test_roots else []
                    proven_updates[qid] = (test_ids, entry, covered, tsources)
            elif lock_node.get("status") == "proven":
                # E1.S8.A8: the bound proving test is now failed/absent → a red test un-proves.
                demotions.add(qid)

    # E1.S10.A2 — inflation warning (non-fatal; runs regardless of failures)
    max_share = args.max_test_share
    for _warn_tid, _warn_count in sorted(test_ac_shares.items()):
        if _warn_count > max_share:
            print(
                f"  INFLATION-WARN: test '{_warn_tid}' is bound to {_warn_count} ACs "
                f"(> {max_share}) — one test rarely proves that many"
            )

    # Each AC is proven independently (E1.S8.A2: a failed/absent bound test refuses THAT AC,
    # not the others). Apply every genuinely-passing proof, then report the refused ones and
    # exit non-zero if any were refused.
    # E1.S8.A8: demote any locked-proven AC whose bound test is now red/absent/uncovered.
    demotions = {q for q in demotions if lock_nodes.get(q, {}).get("status") == "proven"}

    # Mass-demote guard: refuse a run that would demote EVERY currently-proven node at once.
    # That pattern — the whole proven set losing its trigger in one pass — is almost always a
    # configuration error (e.g. --src-root / config srcRoot resolving to the wrong directory
    # and filtering ALL coverage out, so every AC hits NO-COVERAGE) rather than a genuine
    # regression across the entire proven set simultaneously. Fail loudly and refuse to write
    # the lock instead of silently demoting everything; --allow-mass-demote overrides for the
    # rare case a whole-set demotion really is intended.
    proven_before = {q for q, v in lock_nodes.items() if v.get("status") == "proven"}
    if (demotions and len(proven_before) > 1 and demotions == proven_before
            and not getattr(args, "allow_mass_demote", False)):
        print(
            f"prove: REFUSING — this run would demote ALL {len(proven_before)} currently-proven "
            f"AC(s) to unproven in one pass. That is almost certainly a configuration error "
            f"(e.g. --src-root or facit.config.json's srcRoot pointing at the wrong directory, "
            f"filtering all coverage out) rather than a genuine regression across the whole "
            f"proven set — refusing to write the lock. Pass --allow-mass-demote to force this "
            f"through if a whole-set demotion is truly intended."
        )
        return 1

    for qid in demotions:
        lock_nodes[qid]["status"] = "unproven"
        lock_nodes[qid]["tests"] = []
        lock_nodes[qid].pop("coveredFiles", None)
        lock_nodes[qid].pop("testSources", None)
    for qid, (test_ids, impl_entry, covered, tsources) in proven_updates.items():
        lock_nodes[qid]["status"] = "proven"
        lock_nodes[qid]["tests"] = test_ids
        lock_nodes[qid]["coveredFiles"] = covered
        if tsources:
            lock_nodes[qid]["testSources"] = tsources
    if proven_updates or demotions:
        _write(lock_path, lock_data)
        proven = sum(1 for v in lock_nodes.values() if v.get("status") == "proven")
        if proven_updates:
            print(f"proved {len(proven_updates)} AC(s) → lock now has {proven} proven node(s)")
            for qid in sorted(proven_updates):
                print(f"  proven: {qid}  tests={proven_updates[qid][0]}")
        if demotions:
            print(f"DEMOTED {len(demotions)} AC(s) to unproven "
                  f"(a red/absent/uncovered bound test un-proves its AC):")
            for qid in sorted(demotions):
                print(f"  demoted: {qid}")

    if failures:
        print(f"prove: refused {len(failures)} AC(s) (their bound test failed or was absent):")
        for f in failures:
            print(f)
        return 1

    if not proven_updates:
        print("prove: no proof bindings found to process")
    return 0


def cmd_reverify(args):
    """Reverify: run the configured test command, ingest results+coverage, and prove (E1.S12.A1/A2/A3)."""
    cfg = _read_facit_config(args.root)
    test_command = cfg.get("testCommand")
    if not test_command:
        print("reverify: no testCommand in facit.config.json — add it (a shell template with "
              "{filter} and {outdir} placeholders that runs tests and writes results + coverage)")
        return 1

    filter_item = cfg.get("filterItem", "{test}")
    filter_join = cfg.get("filterJoin", " ")

    # Compile facit + require lock
    nodes, errors, _ = compile_facits(args.root)
    if errors:
        print("facit has errors — fix before reverifying:")
        for e in errors:
            print(f"  ERROR: {e}")
        return 1

    lock_path = _lock_path(args.root)
    if not os.path.exists(lock_path):
        print("no lock yet — run `facit lock` first")
        return 1
    lock_data = _load(lock_path)
    lock_nodes = lock_data.get("nodes", {})

    if not args.impl:
        print("--impl is required (repeatable: --impl path/to/implementation.json)")
        return 1

    # Build local-id → qids mapping
    local_to_qids = {}
    for qid, node in nodes.items():
        if node["kind"] == "ac":
            local_to_qids.setdefault(node["localId"], []).append(qid)

    # Load all impl maps → qid → (test_ids, entry, impl_path_abs)
    qid_to_proof = {}
    for impl_arg in args.impl:
        impl_abs = os.path.abspath(impl_arg)
        if not os.path.exists(impl_abs):
            print(f"--impl file not found: {impl_abs}")
            return 1
        impl_scope = _scope_for_impl(impl_abs)
        impl_data = _load(impl_abs)
        for entry in impl_data.get("entries", []):
            proof = entry.get("proof")
            if not proof:
                continue
            test_ids = proof.get("tests", [])
            if not test_ids:
                continue
            ac_id = entry["acId"]
            if impl_scope:
                candidate = f"{impl_scope}::{ac_id}"
                if candidate in nodes:
                    qid_to_proof[candidate] = (test_ids, entry, impl_abs)
            else:
                matches = local_to_qids.get(ac_id, [])
                if len(matches) == 1:
                    qid_to_proof[matches[0]] = (test_ids, entry, impl_abs)

    # Determine affected ACs
    if args.all:
        affected = set(qid_to_proof.keys())
    elif args.ac:
        affected = set()
        for ac_arg in args.ac:
            if "::" in ac_arg:
                if ac_arg in qid_to_proof:
                    affected.add(ac_arg)
                else:
                    print(f"reverify: --ac {ac_arg} not found in impl or has no proof binding")
            else:
                matches = local_to_qids.get(ac_arg, [])
                in_proof = [m for m in matches if m in qid_to_proof]
                if len(in_proof) == 1:
                    affected.add(in_proof[0])
                elif len(in_proof) > 1:
                    print(f"reverify: --ac {ac_arg} is ambiguous: {in_proof} — use fully-qualified id")
                else:
                    print(f"reverify: --ac {ac_arg} not found in impl or has no proof binding")
    else:
        # Incremental (E1.S12.A3): union of spec-drift, code-drift, test-drift
        affected = set()
        for qid in qid_to_proof:
            live_node = nodes.get(qid)
            lock_node = lock_nodes.get(qid)
            if live_node is None or lock_node is None:
                continue
            # Spec drift: facit node hash changed since lock
            if live_node["hash"] != lock_node.get("hash"):
                affected.add(qid)
                continue
            # Code drift: any coveredFile hash changed
            for cf in lock_node.get("coveredFiles", []):
                abs_path = (cf["path"] if os.path.isabs(cf["path"])
                            else os.path.normpath(os.path.join(REPO_ROOT, cf["path"])))
                if not os.path.exists(abs_path):
                    affected.add(qid)
                    break
                with open(abs_path, "rb") as fh:
                    cur_hash = hashlib.sha256(fh.read()).hexdigest()[:16]
                if cur_hash != cf["hash"]:
                    affected.add(qid)
                    break
            if qid in affected:
                continue
            # Test drift: any testSource hash changed
            for ts in lock_node.get("testSources", []):
                now_hash = _test_source_hash_now(ts)
                if now_hash is None or now_hash != ts.get("hash"):
                    affected.add(qid)
                    break

    if not affected:
        print("reverify: nothing to re-run (no drift detected)")
        return 0

    src_root = _resolve_src_root(args)

    # E1.S12.A2 — BATCHED: one test-command invocation for the WHOLE affected set. Union the
    # affected criteria's tests into a single {filter}, run the command ONCE, then attribute each
    # criterion's outcome + coverage from that one run — N affected ACs cost one test invocation,
    # not N. (Per-AC coverage stays correct: coverage.py contexts are matched per test id, and
    # aggregate cobertura is intersected with each AC's own declared code evidence.)
    affected_list = [q for q in sorted(affected) if q in qid_to_proof]
    if not affected_list:
        print("reverify: reverified 0, refused 0")
        return 0

    # Build the filter test-ids (E1.S12.A8): strip a parameterized-test suffix like
    # `(fixtureName: "x")` — the runner filters [Theory] variants by the method's fully-qualified
    # name, and the raw parameter string contains characters (parens, quotes, colons) that break
    # the filter grammar (dotnet test errors, the test never runs, and the AC is WRONGLY demoted).
    # prove still matches the full parameterized name in the results, so only the filter is stripped.
    def _filter_id(_t):
        return _t.split("(", 1)[0]
    _seen = set()
    all_tests = []
    for qid in affected_list:
        for t in qid_to_proof[qid][0]:
            fid = _filter_id(t)
            if fid not in _seen:
                _seen.add(fid)
                all_tests.append(fid)
    # E1.S12.A7 — chunk the affected tests into bounded batches so a very large affected set
    # never produces a filter the test runner can't handle; N ACs cost ceil(N/chunk) invocations,
    # still far fewer than N. chunk size is configurable (reverifyChunkSize, default 40).
    try:
        chunk_size = int(cfg.get("reverifyChunkSize", 40))
    except (TypeError, ValueError):
        chunk_size = 40
    if chunk_size < 1:
        chunk_size = 1
    try:
        max_chars = int(cfg.get("reverifyMaxFilterChars", 1500))
    except (TypeError, ValueError):
        max_chars = 1500
    if max_chars < 1:
        max_chars = 1500
    # Bound each chunk by BOTH count (chunk_size) and filter LENGTH (max_chars): a filter of long
    # test ids (e.g. fully-qualified .NET names) must never overrun the runner and get silently
    # truncated — that would drop tests, read them as absent, and WRONGLY demote passing ACs.
    chunks = []
    _cur, _cur_len = [], 0
    _jlen = len(filter_join)
    for t in all_tests:
        _ilen = len(filter_item.format(test=t)) + _jlen
        if _cur and (len(_cur) >= chunk_size or _cur_len + _ilen > max_chars):
            chunks.append(_cur)
            _cur, _cur_len = [], 0
        _cur.append(t)
        _cur_len += _ilen
    if _cur:
        chunks.append(_cur)
    if not chunks:
        chunks = [[]]

    outdirs = []
    results_files = []
    coverage_files = []
    try:
        for chunk in chunks:
            filt = filter_join.join(filter_item.format(test=t) for t in chunk)
            outdir = tempfile.mkdtemp(prefix="facit-reverify-")
            outdirs.append(outdir)
            cmd = test_command.replace("{filter}", filt).replace("{outdir}", outdir)
            subprocess.run(cmd, shell=True, cwd=REPO_ROOT)

            # Discover this chunk's results + coverage; accumulate across chunks (cmd_prove merges
            # repeated --results / --coverage, so every discovered artifact is passed through).
            for walk_root, _, walk_files in os.walk(outdir):
                for fname in sorted(walk_files):
                    fpath = os.path.join(walk_root, fname)
                    if fname.endswith(".cobertura.xml"):
                        coverage_files.append(fpath)
                    elif fname == ".coverage" or fname.endswith(".coverage"):
                        coverage_files.append(fpath)
                    elif fname.endswith(".trx") or (fname.startswith("junit") and fname.endswith(".xml")) \
                            or fname.endswith(".results.xml"):
                        results_files.append(fpath)
                    elif fname.endswith(".xml"):
                        try:
                            tag = ET.parse(fpath).getroot().tag
                            tag = tag.split("}", 1)[-1] if "}" in tag else tag
                            if tag in ("testsuites", "testsuite", "TestRun"):
                                results_files.append(fpath)
                        except Exception:
                            pass

        if not results_files:
            print(f"REVERIFY-NO-RESULTS: {len(affected_list)} affected AC(s), no results produced")
            return 1

        # Prove per source impl (a subset of that impl's affected ACs) from the shared results —
        # subsets keep cmd_prove scoped so it never demotes an untouched AC, and _scope_for_impl
        # resolves each subset's scope from its own dir (the root dir has no facit.json).
        from collections import defaultdict as _defaultdict
        by_impl = _defaultdict(list)
        for qid in affected_list:
            _tids, entry, impl_abs = qid_to_proof[qid]
            by_impl[impl_abs].append(entry)

        overall_rc = 0
        for impl_abs, entries in by_impl.items():
            subset_impl = {"schemaVersion": 1, "facitVersion": "reverify", "entries": entries}
            subset_path = os.path.join(os.path.dirname(impl_abs), "_reverify_batch.json")
            try:
                _write(subset_path, subset_impl)
                prove_args = argparse.Namespace(
                    root=args.root, verify=False, impl=[subset_path],
                    results=results_files, coverage=coverage_files, src_root=src_root,
                    test_root=[], max_test_share=9999,
                )
                if cmd_prove(prove_args) != 0:
                    overall_rc = 1
            finally:
                try:
                    os.unlink(subset_path)
                except OSError:
                    pass

        final_nodes = _load(_lock_path(args.root)).get("nodes", {})
        reverified = sum(1 for q in affected_list if final_nodes.get(q, {}).get("status") == "proven")
        refused = len(affected_list) - reverified
        print(f"reverify: reverified {reverified}, refused {refused}")
        return overall_rc
    finally:
        for _od in outdirs:
            shutil.rmtree(_od, ignore_errors=True)


def _resolve_root(raw_root):
    """Resolve --root: absolute as-is; relative resolved from repo root."""
    if os.path.isabs(raw_root):
        return raw_root
    return os.path.normpath(os.path.join(REPO_ROOT, raw_root))


def main():
    p = argparse.ArgumentParser(prog="facit", description="manage the structured facit")
    p.add_argument(
        "--root",
        default=DEFAULT_FACIT_DIR,
        help="facit root directory (default: docs/facit relative to repo root). "
             "The lock file is <root>/facit.lock.json. "
             "The JSON Schema is always loaded from docs/facit/schema/.",
    )
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("compile"); c.add_argument("--out"); c.set_defaults(fn=cmd_compile)
    sub.add_parser("validate").set_defaults(fn=cmd_validate)
    sub.add_parser("lock").set_defaults(fn=cmd_lock)
    sub.add_parser("diff").set_defaults(fn=cmd_diff)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    g = sub.add_parser("gap"); g.add_argument("file", nargs="?"); g.set_defaults(fn=cmd_gap)
    pv = sub.add_parser("prove", description="prove ACs from implementation maps + TRX results, or verify the lock")
    pv.add_argument("--impl", action="append", default=[], metavar="PATH",
                    help="implementation.json path (repeatable)")
    pv.add_argument("--results", metavar="TRX_PATH", action="append",
                    help="path to a .trx test-results file (repeatable; proofs may span test projects)")
    pv.add_argument("--verify", action="store_true",
                    help="verify lock: error on proven nodes with no binding or dirty hash")
    pv.add_argument("--coverage", action="append", default=[], metavar="PATH",
                    help="coverage file (repeatable; .coverage for coverage.py or cobertura .xml)")
    pv.add_argument("--src-root", metavar="DIR", default=None,
                    help="restrict coveredFiles to files under this dir (default: config srcRoot, else facit root's parent)")
    pv.add_argument("--test-root", action="append", default=[], metavar="DIR",
                    help="dir to search for proving-test source (repeatable; adds to facit.config.json testRoots)")
    pv.add_argument("--max-test-share", type=int, default=5, metavar="N",
                    help="warn if a test id is bound to more than N ACs (default: 5)")
    pv.add_argument("--allow-mass-demote", action="store_true",
                    help="override the mass-demote refusal: allow a prove run to demote EVERY "
                         "currently-proven node at once (default: refused as a likely "
                         "configuration error, e.g. a wrong --src-root)")
    pv.set_defaults(fn=cmd_prove)
    sub.add_parser("conform", description=(
        "check code drift: verify covered-file hashes for all proven lock nodes"
    )).set_defaults(fn=cmd_conform)
    rv = sub.add_parser("reverify", description=(
        "run the configured test command for affected ACs and prove them "
        "(E1.S12.A1/A2/A3)"
    ))
    rv.add_argument("--impl", action="append", default=[], metavar="PATH",
                    help="implementation.json path (repeatable)")
    rv.add_argument("--all", action="store_true",
                    help="re-run every impl AC that has a proof.tests binding")
    rv.add_argument("--ac", action="append", default=[], metavar="ID",
                    help="re-run this specific AC id (repeatable; bare or scope-qualified)")
    rv.add_argument("--src-root", metavar="DIR", default=None,
                    help="restrict coveredFiles to files under this dir "
                         "(default: config srcRoot, else facit root's parent)")
    rv.add_argument("--test-root", action="append", default=[], metavar="DIR",
                    help="dir to search for proving-test source (repeatable; "
                         "adds to facit.config.json testRoots)")
    rv.set_defaults(fn=cmd_reverify)
    args = p.parse_args()
    # Resolve root to an absolute path (relative roots resolve from repo root)
    args.root = _resolve_root(args.root)
    sys.exit(args.fn(args))


if __name__ == "__main__":
    main()
