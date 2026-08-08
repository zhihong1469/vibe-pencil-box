"""C#-Python bridge entry point.

Reads a JSON request from stdin, dispatches to the requested script, and
writes a JSON response to stdout.
"""
import sys
import os
import json
import base64
import traceback

# Ensure this directory is on sys.path so `import scripts.xxx` works
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)


def main():
    request_line = sys.stdin.readline().strip()
    if not request_line:
        print(json.dumps({"status": "error", "error": "No input received"}))
        return

    try:
        request = json.loads(request_line)
        script_name = request.get("script", "")
        params = request.get("params", {})
        input_file = request.get("input_file", "")
        input_data_b64 = request.get("input_data", "")

        if not script_name:
            print(json.dumps({"status": "error", "error": "Missing 'script' field"}))
            return

        import importlib
        module = importlib.import_module(f"scripts.{script_name}")

        if input_file:
            result = module.process_file(input_file, **params)
        elif input_data_b64:
            raw = base64.b64decode(input_data_b64)
            result = module.process_raw(raw, **params)
        else:
            result = module.run(**params)

        print(json.dumps(
            {"status": "ok", "result": result},
            ensure_ascii=False, default=_json_default
        ))

    except Exception as e:
        print(json.dumps({
            "status": "error",
            "error": str(e),
            "traceback": traceback.format_exc()
        }, ensure_ascii=False))


def _json_default(obj):
    """Handle numpy types and other non-serializable objects."""
    try:
        import numpy as np
        if isinstance(obj, (np.integer,)):
            return int(obj)
        if isinstance(obj, (np.floating,)):
            return float(obj)
        if isinstance(obj, np.ndarray):
            return obj.tolist()
    except ImportError:
        pass
    return str(obj)


if __name__ == "__main__":
    main()
