"""Viewer parity harness: the reference viewer and the port over the same HTTP requests.

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\viewer_parity.py [name ...]

A viewer is a server, not a command, so this harness starts each side once — the models load
once each — and drives both with the same sequence of requests.  ``parity.py`` supplies the
report and the diff; the cases live here.

What is compared: the status, the content type, and the body, case by case, plus the console
lines each case made each server write.  A JSON body is parsed and re-dumped, so the **key
order** is part of the comparison (the reference's dicts are built in a fixed order and the
port's records are declared in it), with the two things that cannot match replaced:

* the JPEG preview — Python encodes with OpenCV's libjpeg, the port with ImageSharp;
* the timing *values* — their keys and their order are kept, so a step appearing or
  disappearing is still caught.

The upload ids are random (``uuid4().hex[:12]``) and differ between the two sides, so every id
a side handed out is replaced wherever it appears — in the answer and in the console line that
quotes it.

Numbers are compared as values, not as spellings: the reference's ``json.dumps`` writes
``556.0`` where the shortest round-trip spelling is ``556``, and the port (``System.Text.Json``,
step 220) writes the latter.  Everything else about a number — its exact double value — is
compared.

    --list              the cases
    NAME ...            case names or globs, default all
"""

from __future__ import annotations

import argparse
import base64
import http.client
import json
import os
import re
import socket
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Sequence

import parity

ROOT = Path(__file__).resolve().parents[3]
PY = ROOT / "tools" / "pyenv" / "Scripts" / "python.exe"
EXE = ROOT / "src" / "Indra.Ocr.Viewer" / "bin" / "Debug" / "net10.0" / "indra-ocr-viewer.exe"
MODELS = ROOT / "resources" / "models"
FIXTURE = ROOT / "src" / "Indra.Ocr.Tests" / "fixtures" / "viewer.json"
WORK = ROOT / "temp" / "viewer_parity"

ENV = dict(os.environ)
ENV["PYTHONPATH"] = str(ROOT / "resources" / "indra-src")
ENV["PYTHONIOENCODING"] = "utf-8"
ENV["COLUMNS"] = "80"

#: A "page N: ..." console line: only the wall-clock total differs.
_PAGE_LINE = re.compile(rb"^(page \d+: \w+, \d+ lines, \d+ blocks, )[0-9.]+( ms .*)$",
                        re.MULTILINE)
#: The banner's port: the two sides cannot listen on the same one.
_BANNER_PORT = re.compile(rb"http://localhost:\d+")


# ======================================================================= the answers


@dataclass
class Step:
    """One request's answer.

    ``exclude`` names something that cannot match and is left out of the body comparison (the
    status and the content type are still compared).
    """

    label: str
    status: int
    content_type: str
    body: bytes
    exclude: str | None = None


@dataclass(frozen=True)
class Case:
    """A named sequence of requests, driven against both servers.

    ``console_exclude`` names something in the console lines this case makes the servers write
    that cannot match; those lines are then left out of the comparison.
    """

    name: str
    run: Callable[["Session"], None]
    console_exclude: str | None = None


class Session:
    """One server, driven by one case: performs the requests and records the answers."""

    def __init__(self, conn: http.client.HTTPConnection, inputs: dict[str, bytes]) -> None:
        self.conn = conn
        self.inputs = inputs
        self.steps: list[Step] = []
        self.ids: list[str] = []  # every id this side handed out (they are random)

    def request(self, label: str, method: str, path: str, body: bytes = b"",
                exclude: str | None = None) -> Step:
        self.conn.request(method, path, body=body or None)
        response = self.conn.getresponse()
        step = Step(label, response.status, response.getheader("Content-Type") or "",
                    response.read(), exclude)
        self.steps.append(step)
        return step

    def get(self, label: str, path: str) -> Step:
        return self.request(label, "GET", path)

    def post(self, label: str, path: str, body: bytes = b"",
             exclude: str | None = None) -> Step:
        return self.request(label, "POST", path, body, exclude)

    def upload(self, name: str) -> str:
        """POST /upload for one of the fixture's inputs; returns the id it gave back."""
        answer = json.loads(self.post(f"upload-{name}", "/upload", self.inputs[name]).body)
        self.ids.append(answer["id"])
        return answer["id"]

    def page(self, label: str, did: str, n: int, opts: dict | None = None,
             exclude: str | None = None) -> Step:
        body = json.dumps({"id": did, "n": n, "opts": opts or {}}).encode()
        return self.post(label, "/page", body, exclude)


# ======================================================================= the cases


def case_info(s: Session) -> None:
    s.get("info", "/info")


def case_page(s: Session) -> None:
    """The page itself: 22 kB of HTML with the logo in it, compared byte for byte."""
    s.get("page", "/")
    s.get("index", "/index.html")


def case_not_found(s: Session) -> None:
    s.get("get-unknown", "/nope")
    s.post("post-unknown", "/nope", b"{}")


def case_errors(s: Session) -> None:
    did = s.upload("text")
    s.page("bad-mode", did, 0, {"mode": "xml"})
    s.page("unknown-upload", "nope", 0)
    s.post("no-id", "/page", b'{"n": 0}')
    s.post("no-n", "/page", json.dumps({"id": did}).encode())
    s.post("garbage-upload", "/upload", b"this is not an image at all")


def case_bad_json(s: Session) -> None:
    # The reference parses the body with json.loads, the port with System.Text.Json: the status
    # and the {"error": …} shape match, the parser's own wording does not.
    s.post("bad-json", "/page", b"{not json", exclude="the JSON parser's wording")


def case_upload_and_page(s: Session) -> None:
    for name in s.inputs:
        s.page(f"page-{name}", s.upload(name), 0)


def case_page_options(s: Session) -> None:
    did = s.upload("text")
    s.page("default", did, 0)
    s.page("mode-text", did, 0, {"mode": "text"})
    s.page("mode-ocr", did, 0, {"mode": "ocr"})
    s.page("model-order", did, 0, {"model_order": True})
    s.page("no-image-ocr", did, 0, {"image_ocr": False})
    s.page("dpi-100", did, 0, {"dpi": 100})
    s.page("furniture", did, 0, {"furniture": True})


def case_page_2(s: Session) -> None:
    s.page("page-2", s.upload("text"), 1)


def case_five_uploads(s: Session) -> None:
    """Six uploads: the first has to be gone (the engine keeps the last five)."""
    first = s.upload("text")
    for name in list(s.inputs)[1:]:
        s.upload(name)
    s.page("first-is-gone", first, 0)


CASES = [
    Case("info", case_info),
    Case("page", case_page),
    Case("not-found", case_not_found),
    Case("errors", case_errors),
    Case("bad-json", case_bad_json, console_exclude="the JSON parser's wording"),
    Case("upload-and-page", case_upload_and_page),
    Case("page-options", case_page_options),
    Case("page-2", case_page_2),
    Case("five-uploads", case_five_uploads),
]


# ======================================================================= the comparison


def canonical(body: bytes, content_type: str, ids: Sequence[str] = ()) -> bytes:
    """The body as both sides can be compared (see the module docstring).

    The ids are only taken out of JSON bodies: the page carries an 8 kB base64 logo, and a random
    id could in principle spell itself inside it — on one side only, which would fail the
    comparison for no reason.
    """
    if not content_type.startswith("application/json"):
        return body
    data = _floatify(_mask(json.loads(_without_ids(body, ids).decode("utf-8"))))
    return json.dumps(data, ensure_ascii=False, indent=1).encode("utf-8")


def console_mask(data: bytes, ids: Sequence[str] = ()) -> bytes:
    """Hide what a server's console cannot share: the ids, the timings, the port it listens on."""
    data = _without_ids(data, ids)
    return _BANNER_PORT.sub(b"http://localhost:<PORT>", _PAGE_LINE.sub(rb"\1<TIMING>\2", data))


def _without_ids(data: bytes, ids: Sequence[str]) -> bytes:
    for did in ids:
        data = data.replace(did.encode(), b"<id>")
    return data


def _mask(value):
    """Replace the JPEG preview and the timing values, keeping every key and its order."""
    if isinstance(value, dict):
        for key, item in list(value.items()):
            if key == "img" and isinstance(item, str) and item.startswith("data:image/jpeg;base64,"):
                value[key] = "<jpeg>"
            elif key == "timings" and isinstance(item, dict):
                value[key] = {k: 0.0 for k in item}
            else:
                _mask(item)
    elif isinstance(value, list):
        for item in value:
            _mask(item)
    return value


def _floatify(value):
    """Every number as a float: 556 and 556.0 are the same value (see the docstring)."""
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        return float(value)
    if isinstance(value, list):
        return [_floatify(item) for item in value]
    if isinstance(value, dict):
        return {key: _floatify(item) for key, item in value.items()}
    return value


def compare(case: Case, reference: Session, port: Session) -> list[str]:
    """The lines describing how the two sides' answers differ."""
    details: list[str] = []
    if len(reference.steps) != len(port.steps):
        details.append(f"     steps: reference {len(reference.steps)}, port {len(port.steps)}")
    for expected, actual in zip(reference.steps, port.steps):
        if expected.label != actual.label:
            details.append(f"     step labels: reference {expected.label}, port {actual.label}")
            continue
        if expected.status != actual.status:
            details.append(f"     {expected.label}: status reference {expected.status}, "
                           f"port {actual.status}")
        if expected.content_type != actual.content_type:
            details.append(f"     {expected.label}: content-type reference "
                           f"{expected.content_type!r}, port {actual.content_type!r}")
        if expected.exclude:
            continue
        details += parity.diff(
            f"{case.name} {expected.label}",
            canonical(expected.body, expected.content_type, reference.ids),
            canonical(actual.body, actual.content_type, port.ids))
    return details


# ======================================================================= the servers


@dataclass
class Server:
    """One side: the process, the port it listens on, and the files its streams go to."""

    process: subprocess.Popen
    port: int
    stdout: Path
    stderr: Path
    connection: http.client.HTTPConnection = field(init=False)

    def __post_init__(self) -> None:
        self.connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=600)

    def wait(self, name: str, timeout: float = 180) -> None:
        """Poll GET /info until the server answers (it loads the models first)."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise SystemExit(f"{name} exited at startup ({self.process.returncode}):\n"
                                 + self.stderr.read_text(encoding="utf-8", errors="replace"))
            try:
                connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=2)
                connection.request("GET", "/info")
                connection.getresponse().read()
                connection.close()
                return
            except OSError:
                time.sleep(0.2)
        raise SystemExit(f"{name} did not answer within {timeout:g}s")

    def stop(self) -> None:
        self.connection.close()
        self.process.terminate()
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=20)


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def reference_command(port: int) -> list[str]:
    """The reference as its console script runs it: prog ``indra-ocr-viewer``, ``main()``."""
    argv = ["indra-ocr-viewer", "--model-dir", str(MODELS), "--port", str(port)]
    code = ("import sys\n"
            f"sys.argv = {argv!r}\n"
            "from indra_ocr.viewer import main\n"
            "main()\n")
    return [str(PY), "-c", code]


def port_command(port: int) -> list[str]:
    return [str(EXE), "--model-dir", str(MODELS), "--port", str(port)]


def start(command: list[str], port: int, stem: str) -> Server:
    stdout = WORK / f"{stem}.out"
    stderr = WORK / f"{stem}.err"
    process = subprocess.Popen(command, stdout=stdout.open("wb"), stderr=stderr.open("wb"),
                               env=ENV, cwd=ROOT)
    return Server(process, port, stdout, stderr)


# ======================================================================= the run


def load_inputs() -> dict[str, bytes]:
    """The documents the C# engine tests use, so the two harnesses cover the same ground."""
    document = json.loads(FIXTURE.read_text(encoding="utf-8"))
    return {name: base64.b64decode(spec["b64"]) for name, spec in document["inputs"].items()}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Run the viewer's HTTP parity harness.")
    parser.add_argument("--list", action="store_true", help="list the cases and exit")
    parser.add_argument("names", nargs="*", metavar="NAME",
                        help="case names or globs (default: all)")
    parsed = parser.parse_args(argv)

    if parsed.list:
        for case in CASES:
            print(case.name)
        return 0
    cases = parity.select(CASES, parsed.names)

    if not EXE.exists():
        raise SystemExit(f"the port is not built: {EXE}\n"
                         "run: dotnet build src\\Indra.Ocr.slnx")

    WORK.mkdir(parents=True, exist_ok=True)
    inputs = load_inputs()
    report = parity.Report()

    reference_port, port_port = free_port(), free_port()
    reference = start(reference_command(reference_port), reference_port, "ref")
    port = start(port_command(port_port), port_port, "port")
    try:
        reference.wait("the reference")
        port.wait("the port")
        # The console is compared per case: a case's lines are what each server wrote while it
        # ran (both sides are driven one request at a time, so the order is the same).
        reference_seen = port_seen = 0
        for case in cases:
            ref_session = Session(reference.connection, inputs)
            port_session = Session(port.connection, inputs)
            case.run(ref_session)
            case.run(port_session)

            ref_lines = reference.stdout.read_bytes()[reference_seen:]
            port_lines = port.stdout.read_bytes()[port_seen:]
            reference_seen += len(ref_lines)
            port_seen += len(port_lines)

            details = compare(case, ref_session, port_session)
            if case.console_exclude is None:
                details += parity.diff(
                    f"{case.name} console",
                    console_mask(ref_lines, ref_session.ids),
                    console_mask(port_lines, port_session.ids))
            report.case(case.name, not details, details)
    finally:
        reference.stop()
        port.stop()

    details = parity.diff("console stderr", reference.stderr.read_bytes(),
                          port.stderr.read_bytes())
    report.case("stderr", not details, details)
    return report.summary()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
