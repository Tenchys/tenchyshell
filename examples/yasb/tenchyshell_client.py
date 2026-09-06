"""Cliente mínimo NDJSON para la API local de TenchyShell."""

import json
import win32file

PIPE = r"\\.\pipe\TenchyShell.LocalApi.v1"


def request(method, params=None, request_id="1"):
    message = {"version": 1, "id": request_id, "type": "request", "method": method}
    if params is not None:
        message["params"] = params
    handle = win32file.CreateFile(
        PIPE, win32file.GENERIC_READ | win32file.GENERIC_WRITE, 0, None,
        win32file.OPEN_EXISTING, 0, None
    )
    try:
        win32file.WriteFile(handle, (json.dumps(message) + "\n").encode("utf-8"))
        _, data = win32file.ReadFile(handle, 65536)
        return json.loads(data.decode("utf-8").splitlines()[0])
    finally:
        win32file.CloseHandle(handle)


if __name__ == "__main__":
    print(json.dumps(request("workspaces.get"), ensure_ascii=False, indent=2))
