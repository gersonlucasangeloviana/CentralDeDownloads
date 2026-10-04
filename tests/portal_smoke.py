"""Verifica autenticação e proteção antifalsificação do portal configurado.

Requer CENTRAL_DOWNLOADS_PORTAL_URL e CENTRAL_DOWNLOADS_PORTAL_PASSWORD. Não altera trabalhos.
"""

import http.cookiejar
import os
import re
import urllib.error
import urllib.parse
import urllib.request

base = os.environ["CENTRAL_DOWNLOADS_PORTAL_URL"].rstrip("/")
password = os.environ["CENTRAL_DOWNLOADS_PORTAL_PASSWORD"]
client = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        return None


anonymous = urllib.request.build_opener(NoRedirect)
for path in ("/api/files", "/api/files/example"):
    try:
        anonymous.open(urllib.request.Request(base + path, headers={
            "Cookie": ".AspNetCore.Cookies=expired"
        }))
        raise AssertionError(f"{path} deveria rejeitar a sessão vencida")
    except urllib.error.HTTPError as error:
        assert error.code == 401, (path, error.code)
        assert error.headers.get("X-Portal-Session-Expired") == "1"
        assert ".AspNetCore.Cookies=" in error.headers.get("Set-Cookie", "")


def post(path, form):
    request = urllib.request.Request(
        base + path, urllib.parse.urlencode(form).encode(), method="POST"
    )
    try:
        with client.open(request) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def token(html):
    match = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', html)
    assert match, "Formulário sem token antifalsificação"
    return match.group(1)


with client.open(base + "/login") as response:
    login_page = response.read().decode()
with client.open(base + "/login?expirada=1") as response:
    assert "Sua sessão expirou. Entre novamente." in response.read().decode()

assert post("/login", {"senha": password})[0] == 400
status, home = post("/login", {
    "senha": password, "__RequestVerificationToken": token(login_page)
})
assert status == 200 and "Arquivos gerados" in home

assert post("/logout", {})[0] == 400
status, login_page = post("/logout", {
    "__RequestVerificationToken": token(home)
})
assert status == 200 and "Senha de administrador" in login_page
print("portal login, CSRF e logout: ok")
