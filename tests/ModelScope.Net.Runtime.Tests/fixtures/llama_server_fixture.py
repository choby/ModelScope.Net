#!/usr/bin/env python3
import argparse
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


parser = argparse.ArgumentParser(add_help=False)
parser.add_argument("--model", required=True)
parser.add_argument("--host", required=True)
parser.add_argument("--port", required=True, type=int)
parser.add_argument("--alias")
parser.add_argument("--ctx-size")
parser.add_argument("--threads")
parser.add_argument("--n-gpu-layers")
parser.add_argument("--no-webui", action="store_true")
parser.add_argument("--no-slots", action="store_true")
parser.add_argument("--api-key-file")
arguments, _ = parser.parse_known_args()
api_key = None
if arguments.api_key_file:
    with open(arguments.api_key_file, "r", encoding="utf-8") as key_file:
        api_key = key_file.readline().strip()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path not in ("/health", "/v1/health"):
            self.send_error(404)
            return
        self._json({"status": "ok"})

    def do_POST(self):
        if api_key and self.headers.get("authorization") != f"Bearer {api_key}":
            self.send_error(401)
            return
        length = int(self.headers.get("content-length", "0"))
        request = json.loads(self.rfile.read(length) or b"{}")
        if self.path not in ("/v1/chat/completions", "/v1/completions"):
            self.send_error(404)
            return
        if request.get("stream"):
            body = (
                'data: {"choices":[{"delta":{"content":"fixture"}}]}\n\n'
                "data: [DONE]\n\n"
            ).encode("utf-8")
            self.send_response(200)
            self.send_header("content-type", "text/event-stream")
            self.send_header("content-length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self._json({
            "id": "fixture",
            "choices": [{"message": {"role": "assistant", "content": "fixture"}, "finish_reason": "stop"}],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        })

    def _json(self, value):
        body = json.dumps(value).encode("utf-8")
        self.send_response(200)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format, *_args):
        return


class ReusableServer(ThreadingHTTPServer):
    allow_reuse_address = True


server = ReusableServer((arguments.host, arguments.port), Handler)
print("llama-fixture-ready", flush=True)
print(
    f"parent-probe={os.environ.get('MODELSCOPE_NET_TEST_LLAMA_PARENT_PROBE', 'missing')}",
    flush=True,
)
server.serve_forever()
