"""Host dịch NLLB-200 chạy cục bộ cho Game Translator (Windows).

Lệnh:
  python -I nllb_host.py check
  python -I nllb_host.py serve --port 47811 --model-dir <thư mục> [--device auto] [--dtype auto]
  python -I nllb_host.py bench --model-dir <thư mục> [--input cau.txt] [--device auto] [--beams 1]

Giao thức HTTP (JSON, UTF-8), chỉ nghe 127.0.0.1 (hoặc --host):
  GET  /v1/health    -> {"version":1,"state":"idle|loading|ready|error","model":..,"device":..,"dtype":..,"error":..}
  POST /v1/load      {"modelDir":..,"device":"auto","dtype":"auto"}  -> 202
  POST /v1/translate {"texts":[..],"src":"eng_Latn","tgt":"vie_Latn","beams":1,"maxNewTokens":256}
  POST /v1/shutdown
Mọi request cần "Authorization: Bearer <token>" (token lấy từ biến môi trường GT_HOST_TOKEN);
request có header Origin bị từ chối (chặn trang web trong trình duyệt gọi vào host).
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import queue
import statistics
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PROTOCOL_VERSION = 1
DEFAULT_PORT = 47811

SAMPLE_SENTENCES = [
    "Hello, traveler! Where are you headed?",
    "I can't believe you made it this far.",
    "The ancient door won't open without the key.",
    "Be careful, the bridge is not safe anymore.",
    "Thank you for saving our village.",
    "What do you want me to do with this?",
    "We need to find the princess before sunset.",
    "That sword looks dangerous. Are you sure about this?",
    "Please, take this potion. You'll need it.",
    "The monsters are getting stronger every night.",
    "I've been waiting for you for a very long time.",
    "Don't give up now. We're almost there!",
    "This place feels strangely familiar.",
    "Come back when you've collected ten crystals.",
    "Our only hope is to reach the castle by dawn.",
    "You look tired. Why don't you rest at the inn?",
    "I'll never forgive what they did to my family.",
    "Wait, did you hear that noise?",
    "The treasure is hidden beyond the frozen lake.",
    "Good luck out there, and stay safe.",
]


def log(event: str, **fields) -> None:
    """Log JSON từng dòng ra stdout."""
    print(json.dumps({"t": time.strftime("%H:%M:%S"), "event": event, **fields}, ensure_ascii=False), flush=True)


# ---------------------------------------------------------------- thiết bị


def import_torch():
    import torch  # noqa: PLC0415

    return torch


def detect_devices(torch) -> dict:
    info = {"torch": torch.__version__, "xpu": False, "cuda": False, "devices": []}
    if hasattr(torch, "xpu") and torch.xpu.is_available():
        info["xpu"] = True
        for i in range(torch.xpu.device_count()):
            info["devices"].append({"kind": "xpu", "index": i, "name": torch.xpu.get_device_name(i)})
    if torch.cuda.is_available():
        info["cuda"] = True
        for i in range(torch.cuda.device_count()):
            info["devices"].append({"kind": "cuda", "index": i, "name": torch.cuda.get_device_name(i)})
    return info


def resolve_device(torch, wanted: str) -> tuple[str, str]:
    """Trả về (tên thiết bị torch, mô tả). wanted: auto | xpu | cuda | cpu."""
    order = ["xpu", "cuda", "cpu"] if wanted == "auto" else [wanted]
    for kind in order:
        if kind == "xpu" and hasattr(torch, "xpu") and torch.xpu.is_available():
            return "xpu:0", "xpu:0 " + torch.xpu.get_device_name(0)
        if kind == "cuda" and torch.cuda.is_available():
            return "cuda:0", "cuda:0 " + torch.cuda.get_device_name(0)
        if kind == "cpu":
            return "cpu", "cpu"
    if wanted != "auto":
        log("device_fallback", wanted=wanted)
    return "cpu", "cpu"


def resolve_dtype(torch, wanted: str, device: str):
    if wanted == "float16":
        return torch.float16
    if wanted == "bfloat16":
        return torch.bfloat16
    if wanted == "float32":
        return torch.float32
    return torch.float32 if device == "cpu" else torch.float16


def sync(torch, device: str) -> None:
    if device.startswith("xpu"):
        torch.xpu.synchronize()
    elif device.startswith("cuda"):
        torch.cuda.synchronize()


# ---------------------------------------------------------------- model


class Engine:
    """Giữ model và dịch. Chỉ một luồng worker gọi translate()."""

    def __init__(self) -> None:
        self.state = "idle"
        self.error: str | None = None
        self.model_name = ""
        self.device_label = ""
        self.dtype_name = ""
        self._torch = None
        self._tok = None
        self._model = None
        self._device = "cpu"
        self._lock = threading.Lock()

    def health(self) -> dict:
        return {
            "version": PROTOCOL_VERSION,
            "state": self.state,
            "model": self.model_name,
            "device": self.device_label,
            "dtype": self.dtype_name,
            "error": self.error,
        }

    def load(self, model_dir: str, device: str, dtype: str) -> None:
        with self._lock:
            self.state, self.error = "loading", None
        try:
            torch = import_torch()
            from transformers import AutoModelForSeq2SeqLM, AutoTokenizer  # noqa: PLC0415

            dev, label = resolve_device(torch, device)
            dt = resolve_dtype(torch, dtype, dev)
            started = time.time()
            log("loading", modelDir=model_dir, device=dev, dtype=str(dt))
            tok = AutoTokenizer.from_pretrained(model_dir, src_lang="eng_Latn")
            model = AutoModelForSeq2SeqLM.from_pretrained(model_dir, torch_dtype=dt)
            model = model.to(dev).eval()
            self._torch, self._tok, self._model, self._device = torch, tok, model, dev
            self.model_name = os.path.basename(os.path.normpath(model_dir))
            self.device_label, self.dtype_name = label, str(dt).replace("torch.", "")
            # Dịch thử để khởi động kernel (lần đầu trên XPU rất chậm) rồi mới báo ready.
            warm = time.time()
            self.translate(["Hello, traveler!"], "eng_Latn", "vie_Latn", 1, 32)
            log("ready", loadSec=round(warm - started, 1), warmupSec=round(time.time() - warm, 1))
            with self._lock:
                self.state = "ready"
        except Exception as ex:  # noqa: BLE001
            log("load_error", error=repr(ex))
            with self._lock:
                self.state, self.error = "error", f"{type(ex).__name__}: {ex}"

    def translate(self, texts: list[str], src: str, tgt: str, beams: int, max_new: int | None) -> list[str]:
        torch, tok, model = self._torch, self._tok, self._model
        if model is None:
            raise RuntimeError("model chưa nạp")
        tok.src_lang = src
        forced = tok.convert_tokens_to_ids(tgt)
        if forced is None or forced == tok.unk_token_id:
            raise ValueError(f"mã ngôn ngữ không hợp lệ: {tgt}")
        out: list[str] = []
        with torch.inference_mode():
            for text in texts:
                if not text.strip():
                    out.append("")
                    continue
                enc = tok(text, return_tensors="pt", truncation=True, max_length=256).to(self._device)
                n_in = int(enc["input_ids"].shape[1])
                limit = min(max_new or 256, 256, n_in * 2 + 16)
                gen = model.generate(
                    **enc,
                    forced_bos_token_id=forced,
                    num_beams=max(1, beams),
                    max_new_tokens=limit,
                    no_repeat_ngram_size=3 if n_in > 6 else 0,
                )
                out.append(tok.batch_decode(gen, skip_special_tokens=True)[0].strip())
            sync(torch, self._device)
        return out


# ---------------------------------------------------------------- HTTP


class Job:
    def __init__(self, fn):
        self.fn = fn
        self.done = threading.Event()
        self.result = None
        self.error: Exception | None = None


class Host:
    def __init__(self, token: str, engine: Engine) -> None:
        self.token = token
        self.engine = engine
        self.jobs: "queue.Queue[Job | None]" = queue.Queue()
        self.server: ThreadingHTTPServer | None = None
        self.worker = threading.Thread(target=self._work, daemon=True)
        self.worker.start()

    def _work(self) -> None:
        while True:
            job = self.jobs.get()
            if job is None:
                return
            try:
                job.result = job.fn()
            except Exception as ex:  # noqa: BLE001
                job.error = ex
            finally:
                job.done.set()

    def submit(self, fn) -> Job:
        job = Job(fn)
        self.jobs.put(job)
        return job


def make_handler(host: Host):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_args) -> None:  # tắt log mặc định
            pass

        def _send(self, code: int, obj: dict) -> None:
            data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _authorized(self) -> bool:
            if self.headers.get("Origin") is not None:
                self._send(403, {"error": "origin not allowed"})
                return False
            given = self.headers.get("Authorization", "")
            expected = "Bearer " + host.token
            if not host.token or not hmac.compare_digest(given.encode(), expected.encode()):
                self._send(401, {"error": "unauthorized"})
                return False
            return True

        def _body(self) -> dict:
            n = int(self.headers.get("Content-Length") or 0)
            if n <= 0:
                return {}
            if n > 4 * 1024 * 1024:
                raise ValueError("body quá lớn")
            return json.loads(self.rfile.read(n).decode("utf-8"))

        def do_GET(self) -> None:  # noqa: N802
            if not self._authorized():
                return
            if self.path == "/v1/health":
                self._send(200, host.engine.health())
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self) -> None:  # noqa: N802
            if not self._authorized():
                return
            try:
                body = self._body()
            except Exception as ex:  # noqa: BLE001
                self._send(400, {"error": f"bad request: {ex}"})
                return
            if self.path == "/v1/load":
                self._load(body)
            elif self.path == "/v1/translate":
                self._translate(body)
            elif self.path == "/v1/shutdown":
                self._send(200, {"ok": True})
                threading.Thread(target=host.server.shutdown, daemon=True).start()
            else:
                self._send(404, {"error": "not found"})

        def _load(self, body: dict) -> None:
            model_dir = body.get("modelDir")
            if not model_dir or not os.path.isdir(model_dir):
                self._send(400, {"error": "modelDir không tồn tại"})
                return
            if host.engine.state == "loading":
                self._send(409, {"error": "đang nạp model"})
                return
            host.engine.state = "loading"
            host.submit(lambda: host.engine.load(model_dir, body.get("device", "auto"), body.get("dtype", "auto")))
            self._send(202, {"accepted": True})

        def _translate(self, body: dict) -> None:
            if host.engine.state != "ready":
                self._send(503, {"error": "model chưa sẵn sàng", "state": host.engine.state})
                return
            texts = body.get("texts")
            if not isinstance(texts, list) or not all(isinstance(t, str) for t in texts):
                self._send(400, {"error": "texts phải là mảng chuỗi"})
                return
            src, tgt = body.get("src", "eng_Latn"), body.get("tgt", "vie_Latn")
            beams, max_new = int(body.get("beams", 1)), body.get("maxNewTokens")
            started = time.perf_counter()
            job = host.submit(lambda: host.engine.translate(texts, src, tgt, beams, max_new))
            job.done.wait()
            if job.error is not None:
                log("translate_error", error=repr(job.error))
                self._send(500, {"error": str(job.error)})
                return
            self._send(200, {"translations": job.result, "ms": round((time.perf_counter() - started) * 1000, 1)})

    return Handler


# ---------------------------------------------------------------- lệnh


def cmd_check(_args) -> int:
    try:
        torch = import_torch()
        import transformers  # noqa: PLC0415

        info = detect_devices(torch)
        info["transformers"] = transformers.__version__
        info["python"] = sys.version.split()[0]
        print(json.dumps(info, ensure_ascii=False))
        return 0
    except Exception as ex:  # noqa: BLE001
        print(json.dumps({"error": f"{type(ex).__name__}: {ex}"}, ensure_ascii=False))
        return 1


def cmd_serve(args) -> int:
    token = os.environ.get("GT_HOST_TOKEN", "")
    if not token:
        log("fatal", error="thiếu biến môi trường GT_HOST_TOKEN")
        return 2
    engine = Engine()
    host = Host(token, engine)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(host))
    server.daemon_threads = True
    host.server = server
    log("listening", host=args.host, port=args.port, pid=os.getpid())
    if args.model_dir:
        host.submit(lambda: engine.load(args.model_dir, args.device, args.dtype))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        host.jobs.put(None)
        server.server_close()
    log("stopped")
    return 0


def cmd_bench(args) -> int:
    if args.input:
        with open(args.input, encoding="utf-8") as f:
            sentences = [ln.strip() for ln in f if ln.strip()]
    else:
        sentences = SAMPLE_SENTENCES
    engine = Engine()
    engine.load(args.model_dir, args.device, args.dtype)
    if engine.state != "ready":
        print("Lỗi nạp model:", engine.error, file=sys.stderr)
        return 1
    print(f"Thiết bị: {engine.device_label} ({engine.dtype_name}), beams={args.beams}")
    times, lines = [], []
    for s in sentences:
        t0 = time.perf_counter()
        out = engine.translate([s], "eng_Latn", "vie_Latn", args.beams, None)[0]
        ms = (time.perf_counter() - t0) * 1000
        times.append(ms)
        lines.append(out)
        if len(times) <= 5:
            print(f"{s}\n  -> {out}  ({ms:.0f} ms)")
    print(
        f"{len(times)} câu: TB {statistics.mean(times):.0f} ms · trung vị {statistics.median(times):.0f}"
        f" · nhanh nhất {min(times):.0f} · chậm nhất {max(times):.0f}"
    )
    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
    return 0


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    p = argparse.ArgumentParser(description="Host dịch NLLB-200 cho Game Translator")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("check").set_defaults(fn=cmd_check)

    def common(sp) -> None:
        sp.add_argument("--model-dir", default="")
        sp.add_argument("--device", default="auto", choices=["auto", "xpu", "cuda", "cpu"])
        sp.add_argument("--dtype", default="auto", choices=["auto", "float16", "bfloat16", "float32"])

    s = sub.add_parser("serve")
    common(s)
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=DEFAULT_PORT)
    s.set_defaults(fn=cmd_serve)
    b = sub.add_parser("bench")
    common(b)
    b.add_argument("--input")
    b.add_argument("--output")
    b.add_argument("--beams", type=int, default=1)
    b.set_defaults(fn=cmd_bench)
    args = p.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
