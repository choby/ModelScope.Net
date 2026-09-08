ARG PYTHON_BASE_IMAGE=registry.invalid/modelscope-net/python-base:provide-approved-digest
FROM ${PYTHON_BASE_IMAGE}

ARG WORKER_UID=65532
ARG WORKER_GID=65532

WORKDIR /opt/modelscope-worker
COPY worker/python/requirements.txt ./requirements.txt
RUN python -m pip install --no-cache-dir --disable-pip-version-check -r requirements.txt
RUN PIP_ROOT_USER_ACTION=ignore python -m pip uninstall --yes pip
COPY worker/python/server.py ./server.py
COPY worker/python/image_output.py ./image_output.py
COPY worker/python/ocr_transport.py ./ocr_transport.py
COPY worker/python/audio_transport.py ./audio_transport.py
COPY worker/python/generated ./generated

ENV HOME=/tmp \
    PYTHONNOUSERSITE=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    TOKENIZERS_PARALLELISM=false

USER ${WORKER_UID}:${WORKER_GID}
EXPOSE 50051
ENTRYPOINT ["python", "/opt/modelscope-worker/server.py"]
