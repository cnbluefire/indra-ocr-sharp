"""Shared machinery for the parity harnesses.

A parity harness runs the Python reference implementation and the C# port on the same
input and compares everything the command produces — the exit code, stdout, stderr and
any files it writes — byte for byte.  ``cli_parity.py`` (the command line, step 195) and
``viewer_parity.py`` (the viewer's HTTP surface, step 240) are both built from this
module.

What a harness supplies:

* a list of :class:`Case` — a name, and the argv both sides get;
* ``reference`` and ``port`` — callables turning that argv into a command line;
* ``env`` and ``cwd`` — the environment and the working directory both run in;
* ``mask`` — a ``bytes -> bytes`` function hiding what is allowed to differ (for the CLI,
  the wall-clock timings); everything else is compared exactly.

Cases are independent — each side writes under its own directory, and a case never reads
what another wrote — so they run in parallel: ``--jobs`` sets how many.  A case still runs
its two sides one after the other, so a running case is one process, plus whatever a case
starts by itself (a CLI pool case starts its worker processes).
"""

from __future__ import annotations

import argparse
import fnmatch
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path
from threading import Lock
from typing import Callable, Sequence, TextIO


@dataclass(frozen=True)
class Case:
    """One input on both sides.

    ``argv`` is what the reference gets; ``@OUT@`` is replaced by each side's own output
    directory when ``outputs`` is set, so the two sides never write to the same place.

    ``env`` is extra environment for both sides (``PYTHONUTF8`` for the reference's math
    path, say) and ``skip_if`` returns why the case cannot run here — a folder that is not
    downloaded — or ``None``; a skipped case is reported and neither passes nor fails.
    """

    name: str
    argv: tuple[str, ...]
    outputs: bool = False
    env: dict[str, str] = field(default_factory=dict)
    skip_if: Callable[[], str | None] | None = None


@dataclass
class Result:
    """What one side produced: the exit code, the two streams and any files it wrote."""

    code: int
    stdout: bytes
    stderr: bytes
    files: dict[str, bytes] = field(default_factory=dict)


def diff(label: str, expected: bytes, actual: bytes) -> list[str]:
    """The lines describing the first difference, or ``[]`` when the two are equal."""
    if expected == actual:
        return []

    lines = [f"     {label}: expected {len(expected)} bytes, got {len(actual)}"]
    for i in range(max(len(expected), len(actual))):
        e = expected[i] if i < len(expected) else None
        a = actual[i] if i < len(actual) else None
        if e != a:
            lo = max(0, i - 80)
            lines.append(f"     first difference at byte {i}: expected {e!r}, got {a!r}")
            lines.append(f"     expected ...{expected[lo:i + 80]!r}")
            lines.append(f"     actual   ...{actual[lo:i + 80]!r}")
            break
    return lines


class Report:
    """One line per case, the diff of a failure under it, and the summary."""

    def __init__(self, stream: TextIO | None = None) -> None:
        self._lock = Lock()
        self.stream = stream if stream is not None else sys.stdout
        self.passed = 0
        self.skipped: list[str] = []
        self.failed: list[str] = []

    def case(self, name: str, ok: bool, details: list[str] | None = None) -> bool:
        """Print one case's verdict and, when it failed, why.  Thread-safe."""
        with self._lock:
            print(f"{'OK  ' if ok else 'FAIL'} {name}", file=self.stream, flush=True)
            for line in details or []:
                print(line, file=self.stream, flush=True)
            if ok:
                self.passed += 1
            else:
                self.failed.append(name)
        return ok

    def skip(self, name: str, reason: str) -> None:
        """Print one case as skipped: it needs something this machine does not have."""
        with self._lock:
            print(f"SKIP {name} ({reason})", file=self.stream, flush=True)
            self.skipped.append(name)

    def summary(self) -> int:
        """Print the summary and return the exit code (0 when everything matched)."""
        with self._lock:
            skipped = f", {len(self.skipped)} skipped" if self.skipped else ""
            if self.failed:
                print(f"{len(self.failed)} failed, {self.passed} passed{skipped}: "
                      f"{', '.join(self.failed)}", file=self.stream, flush=True)
                return 1
            print(f"{self.passed} passed{skipped}", file=self.stream, flush=True)
            return 0


class Harness:
    """Runs the cases on both sides and reports the outcome.

    A side that outruns ``timeout`` is killed and reported as having produced nothing, so a
    hung case fails instead of hanging the harness.
    """

    def __init__(
        self,
        *,
        reference: Callable[[list[str]], list[str]],
        port: Callable[[list[str]], list[str]],
        env: dict[str, str],
        cwd: str | Path,
        work: str | Path,
        mask: Callable[[bytes], bytes] | None = None,
        timeout: float = 300,
        jobs: int = 1,
        report: Report | None = None,
    ) -> None:
        self.reference = reference
        self.port = port
        self.env = env
        self.cwd = Path(cwd)
        self.work = Path(work)
        self.mask = mask or (lambda data: data)
        self.timeout = timeout
        self.jobs = max(1, jobs)
        self.report = report or Report()
        self.work.mkdir(parents=True, exist_ok=True)

    def run(self, cases: Sequence[Case]) -> int:
        """Run every case (in parallel when ``jobs > 1``) and return the exit code."""
        if self.jobs == 1:
            for case in cases:
                self._case(case)
        else:
            with ThreadPoolExecutor(max_workers=self.jobs) as pool:
                for _ in pool.map(self._case, cases):
                    pass
        return self.report.summary()

    def _case(self, case: Case) -> bool:
        if case.skip_if is not None:
            reason = case.skip_if()
            if reason:
                self.report.skip(case.name, reason)
                return True
        try:
            return self._compare(case)
        except Exception as exc:
            # A bug in the harness, or an output directory it cannot read, must not abort
            # the other cases: report this one as a failure and carry on.
            import traceback
            details = [f"     harness error: {exc!r}"]
            details += [f"     {line}" for line in traceback.format_exc().splitlines()[-4:]]
            return self.report.case(case.name, False, details)

    def _compare(self, case: Case) -> bool:
        ref_dir = self.work / case.name / "ref" if case.outputs else None
        port_dir = self.work / case.name / "port" if case.outputs else None
        for directory in (ref_dir, port_dir):
            self._clear(directory)

        reference = self._run(case.argv, ref_dir, reference=True, env=case.env)
        port = self._run(case.argv, port_dir, reference=False, env=case.env)

        details: list[str] = []
        if reference.code != port.code:
            details.append(f"     exit code: reference {reference.code}, port {port.code}")
            details.append(f"     argv: {' '.join(case.argv)}")
        details += diff(f"{case.name} stdout",
                        self.mask(reference.stdout), self.mask(port.stdout))
        details += diff(f"{case.name} stderr",
                        self.mask(reference.stderr), self.mask(port.stderr))
        if case.outputs:
            if set(reference.files) != set(port.files):
                details.append(f"     files: reference {sorted(reference.files)}, "
                               f"port {sorted(port.files)}")
            for key in sorted(set(reference.files) & set(port.files)):
                details += diff(f"{case.name} {key}",
                                self.mask(reference.files[key]),
                                self.mask(port.files[key]))

        return self.report.case(case.name, not details, details)

    def _run(self, argv: Sequence[str], out_dir: Path | None, *, reference: bool,
             env: dict[str, str] | None = None) -> Result:
        argv = [a.replace("@OUT@", str(out_dir)) for a in argv]
        command = (self.reference if reference else self.port)(argv)
        run_env = {**self.env, **env} if env else self.env
        try:
            proc = subprocess.run(command, capture_output=True, env=run_env, cwd=self.cwd,
                                  timeout=self.timeout)
            result = Result(proc.returncode, proc.stdout, proc.stderr)
        except subprocess.TimeoutExpired as expired:
            # A hung case (a deadlocked worker pool, say) must not hang the harness: report
            # it as a side that produced nothing, so the case simply fails.
            result = Result(-1, expired.stdout or b"",
                            (expired.stderr or b"") + f"\n[harness] timed out after "
                            f"{self.timeout:g}s\n".encode())

        if out_dir is not None and out_dir.is_dir():
            for path in sorted(out_dir.iterdir()):
                if path.is_file():
                    result.files[path.name] = path.read_bytes()
        return result

    @staticmethod
    def _clear(directory: Path | None) -> None:
        """Empty a case's output directory, so a stale file cannot make it look equal."""
        if directory is None:
            return
        directory.mkdir(parents=True, exist_ok=True)
        for path in directory.iterdir():
            if path.is_file():
                path.unlink()


@dataclass(frozen=True)
class Options:
    """The command line every harness shares."""

    names: list[str]
    jobs: int
    listing: bool


def default_jobs() -> int:
    """One case per core, capped: each loads the models, and pool cases start workers."""
    return min(os.cpu_count() or 1, 8)


def parse(argv: list[str]) -> Options:
    """Parse the harness's own arguments (``--list``, ``--jobs``, ``--serial``, names)."""
    parser = argparse.ArgumentParser(description="Run a parity harness.")
    parser.add_argument("--list", action="store_true", help="list the cases and exit")
    parser.add_argument("--jobs", type=int, default=0, metavar="N",
                        help=f"cases to run at once (default: {default_jobs()})")
    parser.add_argument("--serial", action="store_true",
                        help="run one case at a time (--jobs 1)")
    parser.add_argument("names", nargs="*", metavar="NAME",
                        help="case names or globs (default: all)")
    parsed = parser.parse_args(argv)
    jobs = 1 if parsed.serial else (parsed.jobs or default_jobs())
    if jobs < 1:
        parser.error("--jobs must be at least 1")
    return Options(parsed.names, jobs, parsed.list)


def select(cases: Sequence[Case], patterns: Sequence[str]) -> list[Case]:
    """The cases whose name matches one of ``patterns`` (exact names or globs)."""
    if not patterns:
        return list(cases)
    missing = [p for p in patterns
               if not any(fnmatch.fnmatchcase(c.name, p) for c in cases)]
    if missing:
        raise SystemExit(f"no case matches: {', '.join(missing)}")
    return [c for c in cases if any(fnmatch.fnmatchcase(c.name, p) for p in patterns)]
