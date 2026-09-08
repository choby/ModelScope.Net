import signal
import os
import sys
import time


def stop(_signum, _frame):
    print("stopping", flush=True)
    sys.exit(0)


signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGINT, stop)
print("worker-ready", flush=True)
print(
    f"parent-probe={os.environ.get('MODELSCOPE_NET_TEST_PARENT_PROBE', 'missing')}",
    flush=True,
)

while True:
    time.sleep(0.05)
