"""Grava uma consulta real como "cassete" para os testes de regressão.

    python tools/gravar_cassete.py 0000248-42.2024.8.26.0319 tests/cassettes/tjsp_cpopg.json --instancias 1

Cada requisição feita pelo scraper é registrada (URL, status, corpo). Antes de salvar,
os dados pessoais são pseudonimizados: nomes de partes, advogados e magistrados encontrados
pelo próprio parser viram "PARTE 1", "ADVOGADO 2"…, e CPF, CNPJ e OAB são mascarados.
Processos são públicos, mas o repositório também é — e o teste não precisa do nome real.

Quando um portal mudar de layout, regrave o cassete e ajuste o parser até o teste passar.
"""

from __future__ import annotations

import argparse
import asyncio
import html
import json
import re
import sys
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from juscraper.servico import Juscraper  # noqa: E402

_CPF = re.compile(r"\d{3}\.\d{3}\.\d{3}-\d{2}")
_CNPJ = re.compile(r"\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}")
_OAB_INPUT = re.compile(r'(<input type="hidden" value=")(\d{3,7})([A-Z]{2}")')
_OAB_TEXTO = re.compile(r"OAB [A-Z]{2}\d+[A-Z]?")


class Gravador(httpx.AsyncBaseTransport):
    def __init__(self) -> None:
        # Um cliente (e não um transport cru) para herdar proxy e CA do ambiente.
        self._real = httpx.AsyncClient(follow_redirects=False, timeout=60)
        self.interacoes: list[dict] = []

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        resposta = await self._real.send(request)
        corpo = resposta.content
        self.interacoes.append(
            {
                "method": request.method,
                "url": str(request.url),
                "status": resposta.status_code,
                "location": resposta.headers.get("location"),
                "content_type": resposta.headers.get("content-type", ""),
                "body": corpo.decode(resposta.encoding or "utf-8", errors="replace"),
            }
        )
        # O corpo já vem descomprimido: repassar Content-Encoding faria o cliente
        # descomprimir de novo.
        cabecalhos = [
            (k, v) for k, v in resposta.headers.multi_items()
            if k.lower() not in {"content-encoding", "content-length", "transfer-encoding"}
        ]
        return httpx.Response(resposta.status_code, headers=cabecalhos, content=corpo, request=request)


def pseudonimizar(interacoes: list[dict], nomes: dict[str, str]) -> None:
    # Nomes mais longos primeiro: "MARIA DA SILVA SANTOS" antes de "MARIA DA SILVA".
    ordem = sorted(nomes, key=len, reverse=True)
    for interacao in interacoes:
        corpo = interacao["body"]
        for nome in ordem:
            for variante in {nome, nome.upper(), html.escape(nome), _entidades(nome)}:
                corpo = corpo.replace(variante, nomes[nome])
        corpo = _CPF.sub("000.000.000-00", corpo)
        corpo = _CNPJ.sub("00.000.000/0000-00", corpo)
        corpo = _OAB_INPUT.sub(r"\g<1>999999\g<3>", corpo)
        corpo = _OAB_TEXTO.sub("OAB XX999999", corpo)
        interacao["body"] = corpo


def _entidades(texto: str) -> str:
    """Forma com entidades HTML nomeadas, como o e-SAJ escreve em alguns trechos."""
    return "".join(
        f"&{html.entities.codepoint2name[ord(c)]};" if ord(c) > 127 and ord(c) in html.entities.codepoint2name else c
        for c in texto
    )


async def gravar(numero: str, destino: Path, instancias: tuple[str, ...]) -> None:
    gravador = Gravador()
    resultado = await Juscraper(transport=gravador, cache_ttl=0).consultar(numero, instancias=instancias)

    nomes: dict[str, str] = {}
    contadores = {"PARTE": 0, "ADVOGADO": 0, "MAGISTRADO": 0}

    def registrar(nome: str | None, tipo: str) -> None:
        if nome and nome not in nomes and len(nome) > 3:
            contadores[tipo] += 1
            nomes[nome] = f"{tipo} {contadores[tipo]}"

    for processo in resultado.processos:
        registrar(processo.juiz, "MAGISTRADO")
        for parte in processo.partes:
            registrar(parte.nome, "PARTE")
            for advogado in parte.advogados:
                registrar(advogado.nome, "ADVOGADO")

    pseudonimizar(gravador.interacoes, nomes)
    destino.parent.mkdir(parents=True, exist_ok=True)
    destino.write_text(
        json.dumps({"numero": numero, "instancias": list(instancias), "interacoes": gravador.interacoes},
                   ensure_ascii=False, indent=1),
        encoding="utf-8",
    )
    print(f"{destino}: {len(gravador.interacoes)} requisições, {len(nomes)} nomes pseudonimizados.")


if __name__ == "__main__":
    import html.entities  # noqa: F401  (usado em _entidades)

    parser = argparse.ArgumentParser()
    parser.add_argument("numero")
    parser.add_argument("destino", type=Path)
    parser.add_argument("--instancias", default="1,2")
    args = parser.parse_args()
    asyncio.run(gravar(args.numero, args.destino, tuple(args.instancias.split(","))))
