"""Verifica se um portal PJe já usa a NOVA consulta pública (front-end Angular + API REST).

    python tools/detectar_consulta_pje.py https://pje-consultapublica.tjdft.jus.br

Rode a partir de um servidor NO BRASIL: vários tribunais (TJES, TJRN, TJAM…) recusam
acessos do exterior, e o resultado de fora seria "bloqueado" sem dizer nada sobre a consulta.

Saídas possíveis:
- ``NOVA``: imprime o apiUrl e a linha para ``JUSCRAPER_PJE_REST``;
- ``ANTIGA_COM_CAPTCHA``: consulta JSF com reCAPTCHA — não suportada, por decisão de projeto;
- ``BLOQUEADO``: firewall/WAF ou bloqueio geográfico;
- ``DESCONHECIDO``: nenhum dos padrões; inspecione manualmente.

Faz no máximo 4 requisições, espaçadas.
"""

from __future__ import annotations

import argparse
import re
import sys
import time

import httpx

_API_URL = re.compile(r"apiUrl\s*:\s*['\"]([^'\"]+)['\"]")
_BLOQUEIO = re.compile(
    r"Cloudflare|Just a moment|block access from your country|localizados no exterior|Acesso Bloqueado",
    re.IGNORECASE,
)


def detectar(portal: str) -> tuple[str, str]:
    portal = portal.rstrip("/")
    with httpx.Client(follow_redirects=True, timeout=30, headers={"User-Agent": "juscraper-detector/0.1"}) as http:
        raiz = http.get(f"{portal}/")
        if raiz.status_code == 403 or _BLOQUEIO.search(raiz.text[:20000]):
            return "BLOQUEADO", f"HTTP {raiz.status_code} em {raiz.url}"

        time.sleep(1)
        env = http.get(f"{portal}/assets/env.js")
        if env.status_code == 200 and (achado := _API_URL.search(env.text)):
            api = achado.group(1).rstrip("/")
            time.sleep(1)
            codigo = http.get(f"{api}/v1/config/codigo-tribunal", headers={"Accept": "application/json"})
            detalhe = f"apiUrl={api} (codigo-tribunal HTTP {codigo.status_code}: {codigo.text[:80]})"
            return "NOVA", detalhe

        for caminho in ("/pje/ConsultaPublica/listView.seam", "/1g/ConsultaPublica/listView.seam"):
            time.sleep(1)
            antiga = http.get(f"{portal}{caminho}")
            if antiga.status_code == 200 and "ConsultaPublica" in antiga.text:
                captcha = re.search(r"recaptcha|hcaptcha|captcha", antiga.text, re.IGNORECASE)
                return ("ANTIGA_COM_CAPTCHA" if captcha else "ANTIGA_SEM_CAPTCHA"), str(antiga.url)

    return "DESCONHECIDO", f"raiz HTTP {raiz.status_code}"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("portal", help="URL base do portal de consulta, ex.: https://pje-consultapublica.tjdft.jus.br")
    parser.add_argument("--sigla", help="Sigla do tribunal, para montar a linha de configuração.")
    args = parser.parse_args()

    tipo, detalhe = detectar(args.portal)
    print(f"{tipo}: {detalhe}")
    if tipo == "NOVA":
        api = detalhe.split()[0].removeprefix("apiUrl=")
        print(f'\nJUSCRAPER_PJE_REST="{args.sigla or "TJXX"}={api}|{args.portal.rstrip("/")}"')
    sys.exit(0 if tipo == "NOVA" else 1)
