#!/usr/bin/env python3
"""ModelScope.Net gRPC Python worker.

The echo backend is deterministic and intended for protocol tests. The modelscope
backend loads a downloaded model directory through ModelScope's pipeline API.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hmac
import ipaddress
import json
import pathlib
import signal
import sys
import threading
import uuid
from dataclasses import dataclass
from typing import Any, Iterable

import grpc

WORKER_DIRECTORY = pathlib.Path(__file__).resolve().parent
GENERATED_DIRECTORY = WORKER_DIRECTORY / "generated"
sys.path.insert(0, str(GENERATED_DIRECTORY))

import python_worker_pb2 as messages  # noqa: E402
import python_worker_pb2_grpc as services  # noqa: E402


def _json_default(value: Any) -> Any:
    if hasattr(value, "tolist"):
        return value.tolist()
    if hasattr(value, "item"):
        return value.item()
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def _encode_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        default=_json_default,
    ).encode("utf-8")


def _decode_json(value: bytes) -> Any:
    try:
        return json.loads(value.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise ValueError("payload_json must contain valid UTF-8 JSON") from exception


def _contains_remote_code(model_path: pathlib.Path) -> bool:
    if not model_path.is_dir():
        return False
    markers = ("*.py", "requirements.txt")
    return any(next(model_path.rglob(marker), None) is not None for marker in markers)


def _coerce_parameter_value(value: str) -> Any:
    stripped = value.strip()
    lowered = stripped.lower()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    if lowered in ("null", "none"):
        return None
    if stripped.startswith("{") or stripped.startswith("["):
        try:
            return json.loads(stripped)
        except json.JSONDecodeError:
            return value
    try:
        return int(stripped)
    except ValueError:
        pass
    try:
        return float(stripped)
    except ValueError:
        return value


def _coerce_parameters(parameters: dict[str, str]) -> dict[str, Any]:
    return {key: _coerce_parameter_value(value) for key, value in parameters.items()}


class EchoModel:
    def __init__(
        self,
        model_path: str,
        model_id: str,
        task: str,
        allow_remote_code: bool,
    ) -> None:
        self.model_path = model_path
        self.model_id = model_id
        self.task = task
        self.allow_remote_code = allow_remote_code

    def invoke(self, payload: Any, parameters: dict[str, str]) -> Any:
        return {
            "payload": payload,
            "parameters": _coerce_parameters(parameters),
            "modelId": self.model_id,
            "modelPath": self.model_path,
            "task": self.task,
            "allowRemoteCode": self.allow_remote_code,
            "trustRemoteCode": self.allow_remote_code,
        }

    def stream(self, payload: Any, parameters: dict[str, str]) -> Iterable[Any]:
        yield self.invoke(payload, parameters)


class ModelScopePipelineModel:
    def __init__(self, model_path: str, task: str, allow_remote_code: bool) -> None:
        try:
            from modelscope.pipelines import pipeline
        except ImportError as exception:
            raise RuntimeError(
                "The modelscope backend requires the optional 'modelscope' Python package."
            ) from exception

        arguments: dict[str, Any] = {
            "model": model_path,
            "trust_remote_code": bool(allow_remote_code),
        }
        if task:
            arguments["task"] = task
        if task == "text-to-image-synthesis":
            # The original Diffusers wrapper defaults to pickle weights.
            # Require safetensors for this path; do not retry with pickle.
            arguments["use_safetensors"] = True
        if task == "auto-speech-recognition":
            # A downloaded snapshot is authoritative; do not let FunASR check for
            # package/model updates while loading an offline worker session.
            arguments["disable_update"] = True
        self._task = task
        self._pipeline = pipeline(**arguments)

    def invoke(self, payload: Any, parameters: dict[str, str]) -> Any:
        pipeline_parameters = parameters
        if self._task == "auto-speech-recognition":
            from audio_transport import decode_asr_input
            payload, pipeline_parameters = decode_asr_input(payload, parameters)
        if self._task in ("ocr-detection", "ocr-recognition"):
            from ocr_transport import decode_ocr_input
            payload, _ = decode_ocr_input(payload, parameters)
        if self._task == "text-to-image-synthesis":
            from image_output import prepare_generation_input
            payload = prepare_generation_input(payload, parameters)
        output = self._pipeline(payload, **_coerce_parameters(pipeline_parameters))
        if self._task == "text-to-image-synthesis":
            from image_output import encode_generated_images
            return encode_generated_images(output)
        if self._task in ("ocr-detection", "ocr-recognition"):
            from ocr_transport import normalize_ocr_output
            return normalize_ocr_output(self._task, output)
        if self._task == "auto-speech-recognition":
            from audio_transport import normalize_asr_output
            return normalize_asr_output(output)
        return output

    def stream(self, payload: Any, parameters: dict[str, str]) -> Iterable[Any]:
        output = self.invoke(payload, parameters)
        if isinstance(output, Iterable) and not isinstance(output, (str, bytes, dict)):
            yield from output
            return
        yield output


@dataclass(frozen=True)
class WorkerSession:
    model: Any
    model_id: str
    revision: str
    task: str


class PythonWorkerService(services.PythonWorkerServicer):
    def __init__(
        self,
        backend: str,
        api_key: str | None,
        model_roots: tuple[pathlib.Path, ...],
        max_sessions: int,
        max_request_bytes: int,
    ) -> None:
        self._backend = backend
        self._api_key = api_key
        self._model_roots = model_roots
        self._max_sessions = max_sessions
        self._max_request_bytes = max_request_bytes
        self._sessions: dict[str, WorkerSession] = {}
        self._lock = threading.RLock()

    def Health(self, request: messages.HealthRequest, context: grpc.ServicerContext):
        self._authorize(context)
        return messages.HealthResponse(
            status="ready",
            version="1.0",
            message=f"{self._backend} backend",
        )

    def LoadModel(self, request: messages.LoadModelRequest, context: grpc.ServicerContext):
        self._authorize(context)
        model_path = pathlib.Path(request.model_path).expanduser().resolve()
        if not model_path.exists() or not self._is_allowed_model_path(model_path):
            context.abort(
                grpc.StatusCode.PERMISSION_DENIED,
                "model_path_outside_allowed_roots",
            )
        if not request.allow_remote_code and _contains_remote_code(model_path):
            context.abort(
                grpc.StatusCode.PERMISSION_DENIED,
                "The model contains Python code but allow_remote_code is disabled.",
            )

        try:
            if self._backend == "echo":
                model = EchoModel(
                    str(model_path),
                    request.model_id,
                    request.task,
                    request.allow_remote_code,
                )
            else:
                model = ModelScopePipelineModel(
                    str(model_path),
                    request.task,
                    request.allow_remote_code,
                )
        except Exception as exception:
            context.abort(
                grpc.StatusCode.FAILED_PRECONDITION,
                f"Model load failed: {type(exception).__name__}",
            )

        session_id = uuid.uuid4().hex
        with self._lock:
            if len(self._sessions) >= self._max_sessions:
                context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "worker_session_limit_reached")
            self._sessions[session_id] = WorkerSession(
                model=model,
                model_id=request.model_id,
                revision=request.revision,
                task=request.task,
            )
        return messages.LoadModelResponse(session_id=session_id)

    def Invoke(self, request: messages.InvokeRequest, context: grpc.ServicerContext):
        self._authorize(context)
        self._validate_payload_size(request.payload_json, context)
        session = self._get_session(request.session_id, context)
        try:
            output = session.model.invoke(_decode_json(request.payload_json), dict(request.parameters))
            return messages.InvokeResponse(output_json=_encode_json(output))
        except ValueError as exception:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exception))
        except Exception as exception:
            context.abort(
                grpc.StatusCode.INTERNAL,
                f"Inference failed: {type(exception).__name__}",
            )

    def InvokeStreaming(self, request: messages.InvokeRequest, context: grpc.ServicerContext):
        self._authorize(context)
        self._validate_payload_size(request.payload_json, context)
        session = self._get_session(request.session_id, context)
        try:
            payload = _decode_json(request.payload_json)
            for item in session.model.stream(payload, dict(request.parameters)):
                yield messages.StreamEvent(event="data", data_json=_encode_json(item))
            yield messages.StreamEvent(event="done", data_json=b"null", terminal=True)
        except ValueError as exception:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exception))
        except Exception as exception:
            context.abort(
                grpc.StatusCode.INTERNAL,
                f"Streaming inference failed: {type(exception).__name__}",
            )

    def UnloadModel(self, request: messages.UnloadModelRequest, context: grpc.ServicerContext):
        self._authorize(context)
        with self._lock:
            self._sessions.pop(request.session_id, None)
        return messages.UnloadModelResponse()

    def _get_session(self, session_id: str, context: grpc.ServicerContext) -> WorkerSession:
        with self._lock:
            session = self._sessions.get(session_id)
        if session is None:
            context.abort(grpc.StatusCode.NOT_FOUND, "worker_session_not_found")
        return session

    def _authorize(self, context: grpc.ServicerContext) -> None:
        if self._api_key is None:
            return
        metadata = dict(context.invocation_metadata())
        supplied = metadata.get("authorization", "")
        expected = f"Bearer {self._api_key}"
        if not hmac.compare_digest(supplied, expected):
            context.abort(grpc.StatusCode.UNAUTHENTICATED, "worker_authentication_required")

    def _is_allowed_model_path(self, model_path: pathlib.Path) -> bool:
        return any(model_path == root or root in model_path.parents for root in self._model_roots)

    def _validate_payload_size(self, payload: bytes, context: grpc.ServicerContext) -> None:
        if len(payload) > self._max_request_bytes:
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "worker_request_too_large")


def _parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="ModelScope.Net Python gRPC worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=50051)
    parser.add_argument("--backend", choices=("echo", "modelscope"), default="modelscope")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--api-key-file")
    parser.add_argument("--allow-unauthenticated-loopback", action="store_true")
    parser.add_argument("--tls-cert-file")
    parser.add_argument("--tls-key-file")
    parser.add_argument("--tls-client-ca-file")
    parser.add_argument("--model-root", action="append", default=[])
    parser.add_argument("--max-sessions", type=int, default=4)
    parser.add_argument("--max-request-bytes", type=int, default=4 * 1024 * 1024)
    return parser.parse_args()


def main() -> int:
    arguments = _parse_arguments()
    tls_paths = (
        arguments.tls_cert_file,
        arguments.tls_key_file,
        arguments.tls_client_ca_file,
    )
    tls_value_count = sum(bool(value) for value in tls_paths)
    if tls_value_count not in (0, 3):
        raise ValueError(
            "mTLS requires --tls-cert-file, --tls-key-file and --tls-client-ca-file"
        )
    tls_enabled = tls_value_count == 3
    try:
        address_value = ipaddress.ip_address(arguments.host)
    except ValueError as exception:
        raise ValueError("--host must be an IP address") from exception
    if not address_value.is_loopback and not tls_enabled:
        raise ValueError("A non-loopback Python worker requires mutual TLS")
    if arguments.workers < 1 or arguments.workers > 64:
        raise ValueError("--workers must be between 1 and 64")
    if arguments.max_sessions < 1 or arguments.max_sessions > 1024:
        raise ValueError("--max-sessions must be between 1 and 1024")
    if arguments.max_request_bytes < 1024 or arguments.max_request_bytes > 64 * 1024 * 1024:
        raise ValueError("--max-request-bytes must be between 1024 and 67108864")

    api_key = None
    if arguments.api_key_file:
        api_key = pathlib.Path(arguments.api_key_file).read_text(encoding="utf-8").strip()
        if not api_key:
            raise ValueError("--api-key-file must contain a non-empty key")
    elif not arguments.allow_unauthenticated_loopback:
        raise ValueError(
            "Python worker authentication is required; provide --api-key-file or explicitly opt into "
            "--allow-unauthenticated-loopback for development"
        )

    model_roots = tuple(pathlib.Path(value).expanduser().resolve() for value in arguments.model_root)
    if not model_roots:
        raise ValueError("At least one --model-root is required")
    if any(not root.is_dir() for root in model_roots):
        raise ValueError("Every --model-root must be an existing directory")

    server = grpc.server(
        concurrent.futures.ThreadPoolExecutor(max_workers=arguments.workers),
        options=[
            ("grpc.max_receive_message_length", arguments.max_request_bytes + 65536),
            ("grpc.max_send_message_length", arguments.max_request_bytes + 65536),
        ],
    )
    services.add_PythonWorkerServicer_to_server(
        PythonWorkerService(
            arguments.backend,
            api_key,
            model_roots,
            arguments.max_sessions,
            arguments.max_request_bytes,
        ),
        server,
    )
    address = f"{arguments.host}:{arguments.port}"
    if tls_enabled:
        certificate_chain = pathlib.Path(arguments.tls_cert_file).read_bytes()
        private_key = pathlib.Path(arguments.tls_key_file).read_bytes()
        client_ca = pathlib.Path(arguments.tls_client_ca_file).read_bytes()
        credentials = grpc.ssl_server_credentials(
            ((private_key, certificate_chain),),
            root_certificates=client_ca,
            require_client_auth=True,
        )
        bound_port = server.add_secure_port(address, credentials)
    else:
        bound_port = server.add_insecure_port(address)
    if bound_port == 0:
        raise RuntimeError("Could not bind Python worker endpoint.")

    stopped = threading.Event()

    def stop(signum: int, frame: Any) -> None:
        del signum, frame
        server.stop(grace=5)
        stopped.set()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    server.start()
    transport = "mtls" if tls_enabled else "loopback"
    print(f"worker-ready {address} backend={arguments.backend} transport={transport}", flush=True)
    stopped.wait()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
