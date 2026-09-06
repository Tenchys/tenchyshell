"""Esqueleto de widget YASB para el workspace activo."""

from tenchyshell_client import request


def get_text():
    response = request("workspace.active.get")
    if not response.get("ok"):
        return "Workspace ?"
    return f"Workspace {response['result']['activeWorkspace']}"


# Un widget YASB puede usar get_text() durante su actualización. Para eventos
# push, conserva una conexión abierta y procesa líneas con type == "event".
